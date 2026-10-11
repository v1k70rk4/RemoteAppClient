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

    /// <summary>Asks for a key rotation at the next opportunity (after a device re-key).</summary>
    public void RequestRotation()
    {
        _rotateRequested = true;
        if (_wake.CurrentCount == 0) _wake.Release();
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
                FinishInterruptedRotation();
                var due = _rotateRequested ? "rotate" : SshCertificate.Due(ReadCert(), DateTimeOffset.UtcNow);
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

    /// <summary>One round. True when the certificate on disk is fresh afterwards.</summary>
    private async Task<bool> RunAsync(string due, CancellationToken ct)
    {
        var rotate = due == "rotate";
        string pub;
        if (rotate)
        {
            // The new key is made beside the old one and only moves into place once the server has accepted it:
            // until then every tunnel keeps using the old key, which the server still honours.
            pub = GenerateKey(NextKeyPath);
        }
        else
        {
            pub = File.ReadAllText(PubPath).Trim();
        }

        SshCertResponse? resp;
        using (var http = BuildClient())
        {
            using var r = await http.PostAsJsonAsync($"{BaseUrl}/api/ssh-cert", new SshCertRequest { SshPublicKey = pub }, AgentJsonContext.Default.SshCertRequest, ct);
            if (!r.IsSuccessStatusCode)
            {
                if (rotate) DeleteNext();
                // A server from before this feature has no such endpoint: nothing to do until it is updated, and
                // the long certificate from enrollment serves meanwhile. Not a retry case; the regular check returns.
                if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return true;
                logger.LogWarning(L.SshCertService_ServerRefused, (int)r.StatusCode);
                return false;
            }
            resp = await r.Content.ReadFromJsonAsync(AgentJsonContext.Default.SshCertResponse, ct);
        }
        if (resp is null || string.IsNullOrWhiteSpace(resp.Certificate) || SshCertificate.Parse(resp.Certificate) is null)
        {
            if (rotate) DeleteNext();
            return false;
        }

        if (rotate)
        {
            // Certificate first, beside the new key; then the three moves. The server has already adopted the
            // new key (and revoked the old one), so a crash between the moves is finished at the next start.
            WriteAtomic(NextKeyPath + "-cert.pub", resp.Certificate.Trim() + "\n");
            FinishInterruptedRotation();
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

    /// <summary>A rotation whose certificate landed but whose moves did not all happen: complete it. The
    /// certificate beside the next key is the proof that the server adopted that key.</summary>
    private void FinishInterruptedRotation()
    {
        var nextCert = NextKeyPath + "-cert.pub";
        if (!File.Exists(NextKeyPath) || !File.Exists(nextCert))
        {
            DeleteNext(); // a key without an accepted certificate: the server never adopted it
            return;
        }
        File.Move(NextKeyPath, KeyPath, overwrite: true);
        if (File.Exists(NextKeyPath + ".pub")) File.Move(NextKeyPath + ".pub", PubPath, overwrite: true);
        File.Move(nextCert, CertPath, overwrite: true);
    }

    private void DeleteNext()
    {
        foreach (var p in new[] { NextKeyPath, NextKeyPath + ".pub", NextKeyPath + "-cert.pub" })
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
