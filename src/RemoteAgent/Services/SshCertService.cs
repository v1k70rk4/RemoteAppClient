using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Commands;
using RemoteAgent.Configuration;
using RemoteAgent.Enrollment;
using RemoteAgent.Security;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Services;

/// <summary>
/// Keeps the tunnel key's SSH certificate short-lived. The private key (<c>id_ed25519</c>) stays a file, but its
/// certificate lives days, not years, and a new one is issued only over mTLS - to the device that holds the
/// TPM-bound client certificate. A copy of the key file is therefore worth nothing once its certificate ends,
/// and a device switched off for months simply asks for a fresh certificate when it returns.
///
/// Checked shortly after start and every few hours: a certificate near its end is renewed for the same key; a
/// long-lived one (the 825-day certificate from enrollment) is replaced by a key rotation - new key, new
/// certificate, the previous key on the bastion's revocation list - so copies made before this agent stop
/// working at once. A re-key of the device certificate asks for a rotation too. ssh reads the key and the
/// certificate when it starts a tunnel, so a running tunnel is never touched; the next one uses the new files.
///
/// A rotation is staged: the new key is made as <c>id_ed25519.next</c> and offered to the server; the server may
/// have adopted it (and revoked the old key) even when its answer never arrived, so the staged key is kept and
/// offered again until the server answers with a certificate or refuses it outright. The certificate beside
/// the staged key is the proof of adoption; from then on the moves into place are finished whatever happens.
/// </summary>
public sealed class SshCertService(IOptions<AgentOptions> options, ILogger<SshCertService> logger) : BackgroundService
{
    private readonly AgentOptions _opt = options.Value;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile bool _rotateRequested;

    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);

    private string BaseUrl => _opt.Telemetry.IngestUrl.Replace("/api/telemetry", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
    private string KeyPath => Path.Combine(_opt.EnrollmentDir, "id_ed25519");
    private string PubPath => KeyPath + ".pub";
    private string CertPath => KeyPath + "-cert.pub";
    private string NextKeyPath => KeyPath + ".next";
    private string NextPubPath => NextKeyPath + ".pub";
    private string NextCertPath => NextKeyPath + "-cert.pub";

    /// <summary>Asks for a key rotation at the next opportunity (after a device re-key).</summary>
    public void RequestRotation()
    {
        _rotateRequested = true;
        Nudge();
    }

    /// <summary>Brings the next check forward (the command channel just came up: after a server outage the
    /// certificate may have run out, and the tunnel should not wait hours for the regular check). Cheap when
    /// nothing is due - a file read and a date comparison.</summary>
    public void Nudge()
    {
        // Two callers (the channel, a re-key) may nudge at once; a wake already pending is all either wanted.
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* already pending */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (DeviceIdentity.Current is null || string.IsNullOrWhiteSpace(_opt.Telemetry.IngestUrl) || !File.Exists(KeyPath)) return;
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = CheckEvery;
            try
            {
                FinishAdoptedRotation();
                // A staged key the server may already hold comes before anything the policy says: until the server
                // has answered for it, the key on disk may be the revoked one.
                var due = File.Exists(NextPubPath) || _rotateRequested ? "rotate" : SshCertificate.Due(ReadCert(), DateTimeOffset.UtcNow);
                if (due is not null && !await RunAsync(due, stoppingToken)) wait = RetryAfter;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, L.SshCertService_Failed); wait = RetryAfter; }

            try { await _wake.WaitAsync(wait, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private SshCertificate.Validity? ReadCert()
    {
        try { return File.Exists(CertPath) ? SshCertificate.Parse(File.ReadAllText(CertPath)) : null; }
        catch (IOException) { return null; }
    }

    /// <summary>One round. True when the certificate on disk is fresh afterwards, or there is nothing to do.</summary>
    private async Task<bool> RunAsync(string due, CancellationToken ct)
    {
        var rotate = due == "rotate";
        string pub;
        string? previous = null, proof = null;
        if (rotate)
        {
            // The key in use until now: the server revokes it even when its own record of this device has none,
            // but only against a signature made with it over the new key - its word alone revokes nothing.
            try { previous = File.Exists(PubPath) ? File.ReadAllText(PubPath).Trim() : null; } catch (IOException) { }
            // The staged key from an earlier round is offered again rather than replaced: the server may have
            // adopted it already, and a second new key would revoke a key that was never in use.
            pub = File.Exists(NextPubPath) && File.Exists(NextKeyPath) ? File.ReadAllText(NextPubPath).Trim() : GenerateKey(NextKeyPath);
        }
        else
        {
            pub = File.ReadAllText(PubPath).Trim();
        }
        if (previous is not null && SshCertificate.KeyBody(pub) is { } newBody)
            proof = SignWithKey(KeyPath, newBody);

        SshCertResponse? resp;
        using (var http = BuildClient())
        {
            using var r = await http.PostAsJsonAsync($"{BaseUrl}/api/ssh-cert", new SshCertRequest { SshPublicKey = pub, PreviousSshPublicKey = previous, PreviousKeyProof = proof }, AgentJsonContext.Default.SshCertRequest, ct);
            if (!r.IsSuccessStatusCode)
            {
                var status = (int)r.StatusCode;
                // Only the server's own refusals say it did not take the staged key: a bad request, an unknown or
                // unapproved device, a missing endpoint, a revoked key. A 5xx, a proxy's 408/429 or a lost answer
                // says nothing, and the key stays staged for the next round.
                if (rotate && status is 400 or 401 or 403 or 404 or 409) DeleteStaged();
                // A server from before this feature has no such endpoint: nothing to do until it is updated, and
                // the long certificate from enrollment serves meanwhile. Not a retry case; the regular check returns.
                if (status == 404) return true;
                logger.LogWarning(L.SshCertService_ServerRefused, status);
                return false;
            }
            resp = await r.Content.ReadFromJsonAsync(AgentJsonContext.Default.SshCertResponse, ct);
        }
        if (resp is null || string.IsNullOrWhiteSpace(resp.Certificate) || SshCertificate.Parse(resp.Certificate) is null)
            return false; // a malformed answer: the staged key, if any, is offered again next time

        if (rotate)
        {
            // The certificate beside the staged key is the proof that the server adopted it; the moves follow,
            // and a crash between them is finished at the next start.
            WriteAtomic(NextCertPath, resp.Certificate.Trim() + "\n");
            FinishAdoptedRotation();
            _rotateRequested = false;
            logger.LogWarning(L.SshCertService_Rotated, resp.ValidBefore);
        }
        else
        {
            WriteAtomic(CertPath, resp.Certificate.Trim() + "\n");
            logger.LogInformation(L.SshCertService_Renewed, resp.ValidBefore);
        }
        return true;
    }

    /// <summary>Moves an adopted rotation into place: key, public key, certificate - whichever of them is still
    /// staged. Without a staged certificate nothing is adopted yet, and the staged key is left for the next round.</summary>
    private void FinishAdoptedRotation()
    {
        if (!File.Exists(NextCertPath)) return;
        if (File.Exists(NextKeyPath)) File.Move(NextKeyPath, KeyPath, overwrite: true);
        if (File.Exists(NextPubPath)) File.Move(NextPubPath, PubPath, overwrite: true);
        File.Move(NextCertPath, CertPath, overwrite: true);
    }

    private void DeleteStaged()
    {
        foreach (var p in new[] { NextKeyPath, NextPubPath, NextCertPath })
            try { File.Delete(p); } catch { /* best effort */ }
    }

    private static string GenerateKey(string keyPath)
    {
        foreach (var p in new[] { keyPath, keyPath + ".pub", keyPath + "-cert.pub" })
            if (File.Exists(p)) File.Delete(p);
        var psi = new ProcessStartInfo(SshTools.ResolveSshKeygen()) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add("-t"); psi.ArgumentList.Add("ed25519");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(keyPath);
        psi.ArgumentList.Add("-N"); psi.ArgumentList.Add("");
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("-C"); psi.ArgumentList.Add("remoteagent");
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0 || !File.Exists(keyPath + ".pub"))
            throw new InvalidOperationException(L.EnrollCommand_SshKeygenFailedSSHKey);
        EnrollCommand.TightenAcl(keyPath);
        return File.ReadAllText(keyPath + ".pub").Trim();
    }

    /// <summary>ssh-keygen -Y sign with the key on disk, namespace "remoteagent-rotation", over the message plus a
    /// newline. Null when the installed ssh-keygen cannot sign (older builds): then the previous key simply is not
    /// offered, as before.</summary>
    private static string? SignWithKey(string keyPath, string message)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ra_rot_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var msg = Path.Combine(dir, "msg");
            File.WriteAllText(msg, message + "\n");
            var psi = new ProcessStartInfo(SshTools.ResolveSshKeygen()) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.ArgumentList.Add("-Y"); psi.ArgumentList.Add("sign");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(keyPath);
            psi.ArgumentList.Add("-n"); psi.ArgumentList.Add("remoteagent-rotation");
            psi.ArgumentList.Add(msg);
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(15000);
            return proc.HasExited && proc.ExitCode == 0 && File.Exists(msg + ".sig") ? File.ReadAllText(msg + ".sig") : null;
        }
        catch { return null; }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    private static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    private HttpClient BuildClient()
    {
        var handler = new SocketsHttpHandler();
        handler.SslOptions.ClientCertificates ??= new();
        handler.SslOptions.ClientCertificates.Add(CertHelper.ResolveClientCertificate(_opt.ClientCertPfxPath, _opt.Telemetry.ClientCertThumbprint));
        if (!string.IsNullOrWhiteSpace(_opt.Telemetry.ServerCertPinSha256))
            handler.SslOptions.RemoteCertificateValidationCallback = CertHelper.PinnedServerValidator(_opt.Telemetry.ServerCertPinSha256);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }
}
