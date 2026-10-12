using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Commands;
using RemoteAgent.Configuration;
using RemoteAgent.Enrollment;
using RemoteAgent.Security;
using RemoteAgent.Tunnel;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Services;

/// <summary>
/// Keeps the tunnel key where it belongs and its certificate short-lived.
///
/// The key: a named CNG key (TPM when usable, software provider otherwise) served to ssh.exe by the in-process
/// agent, so no private key file exists; a device still on an OpenSSH key file from an older enrollment is moved
/// over by a rotation. The certificate: three days, fetched over mTLS - that is, only by the device holding the
/// TPM-bound client certificate - and renewed at half-time; a long-lived one (825 days from an old enrollment)
/// is replaced by a rotation too. Checked shortly after start, every six hours, and whenever the command
/// channel comes up.
///
/// A rotation is staged and proven before it counts: the new key is made beside the current one, the server
/// issues a certificate but keeps the current key on record, the agent opens a real connection to the bastion
/// with the new key and only then confirms - at which point the server revokes the previous key and adopts
/// the new one, and the agent switches. A failure anywhere leaves the current key in use. The stage is on file
/// (ssh-rotation.json), so a restart resumes it rather than making another key; a key the bastion refused is
/// not tried again for a day. ssh reads the files when it starts a tunnel, so running tunnels are never touched.
/// </summary>
public sealed class SshCertService(IOptions<AgentOptions> options, TransportState transport, ILoggerFactory loggerFactory, ILogger<SshCertService> logger) : BackgroundService
{
    private readonly AgentOptions _opt = options.Value;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile bool _rotateRequested;

    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FailedBackoff = TimeSpan.FromHours(24);

    private string BaseUrl => _opt.Telemetry.IngestUrl.Replace("/api/telemetry", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
    private string Dir => _opt.EnrollmentDir;
    private string StatePath => Path.Combine(Dir, "ssh-rotation.json");
    private string FileKeyPath => Path.Combine(Dir, "id_ed25519");
    private string CngPubPath => Path.Combine(Dir, SshKeyStore.PublicKeyFile);
    private string CngCertPath => Path.Combine(Dir, SshKeyStore.CertificateFile);

    /// <summary>What a rotation in progress looks like on disk.</summary>
    public sealed class RotationState
    {
        public string Provider { get; set; } = "";       // tpm | software | file
        public string? KeyName { get; set; }             // CNG key name, or null for a staged key file
        public string PublicKeyLine { get; set; } = "";
        public string Stage { get; set; } = "";          // requested | issued | failed
        public bool PendingConfirm { get; set; }         // the server holds the switch until we confirm
        public DateTimeOffset? ValidBefore { get; set; }
        public DateTimeOffset? FailedUtc { get; set; }
    }

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
        if (DeviceIdentity.Current is null || SshIdentity.Current is null || string.IsNullOrWhiteSpace(_opt.Telemetry.IngestUrl)) return;
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = CheckEvery;
            try
            {
                if (!await RoundAsync(stoppingToken)) wait = RetryAfter;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, L.SshCertService_Failed); wait = RetryAfter; }

            try { await _wake.WaitAsync(wait, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One round. False asks for the short retry.</summary>
    private async Task<bool> RoundAsync(CancellationToken ct)
    {
        var state = ReadState();
        if (state is { Stage: "failed" })
        {
            // The bastion refused a new key recently: renewals only, no new key for a day.
            if (state.FailedUtc is { } at && DateTimeOffset.UtcNow - at < FailedBackoff) return await RenewIfDueAsync(ct);
            DeleteState(); state = null;
        }
        if (state is not null) return await ContinueRotationAsync(state, ct);

        var id = SshIdentity.Current!;
        var due = _rotateRequested ? "rotate" : SshCertificate.Due(ReadCert(id.CertificatePath), DateTimeOffset.UtcNow);
        if (due is null && !id.InAgent && CngUsable()) due = "rotate"; // a key file on a machine that can hold the key in CNG
        if (due is null) return true;
        if (due == "renew") return await RenewAsync(id, ct);
        var started = StartRotation(id);
        return started is not null && await ContinueRotationAsync(started, ct);
    }

    private async Task<bool> RenewIfDueAsync(CancellationToken ct)
    {
        var id = SshIdentity.Current!;
        return SshCertificate.Due(ReadCert(id.CertificatePath), DateTimeOffset.UtcNow) == "renew" ? await RenewAsync(id, ct) : true;
    }

    // ---- renewal: same key, fresh certificate ----------------------------------------------------------

    private async Task<bool> RenewAsync(SshIdentity.Snapshot id, CancellationToken ct)
    {
        var pub = File.ReadAllText(id.PublicKeyPath).Trim();
        var (resp, status) = await RequestCertAsync(new SshCertRequest { SshPublicKey = pub }, ct);
        if (resp is null)
        {
            if (status == 404) return true; // a server from before this feature: nothing to do until it is updated
            if (status > 0) logger.LogWarning(L.SshCertService_ServerRefused, status);
            return false;
        }
        WriteAtomic(id.CertificatePath, resp.Certificate.Trim() + "\n");
        logger.LogInformation(L.SshCertService_Renewed, resp.ValidBefore);
        return true;
    }

    // ---- rotation ------------------------------------------------------------------------------------

    /// <summary>Makes the new key beside the current one and records it. CNG when it can; a key file otherwise.</summary>
    private RotationState? StartRotation(SshIdentity.Snapshot current)
    {
        RotationState state;
        try
        {
            using var k = SshKeyStore.Create(preferTpm: DeviceKeyStore.TpmUsable());
            state = new RotationState { Provider = k.Provider, KeyName = k.KeyName, PublicKeyLine = k.PublicKeyLine, Stage = "requested" };
            WriteAtomic(NextPub(state), k.PublicKeyLine + "\n");
        }
        catch (CryptographicException)
        {
            if (current.InAgent) return null; // CNG worked before and refuses now: not a day to move anything
            var pub = GenerateKeyFile(FileKeyPath + ".next");
            state = new RotationState { Provider = DeviceKeyStore.File, PublicKeyLine = pub, Stage = "requested" };
        }
        WriteState(state);
        return state;
    }

    private async Task<bool> ContinueRotationAsync(RotationState state, CancellationToken ct)
    {
        var current = SshIdentity.Current!;
        if (state.Stage == "requested")
        {
            var req = new SshCertRequest { SshPublicKey = state.PublicKeyLine, CanConfirm = true };
            // The key in use until now, with proof of holding it: the server revokes it even when its own record
            // of this device has none. Its word alone revokes nothing.
            if (SshCertificate.KeyBody(state.PublicKeyLine) is { } newBody && Previous(current, newBody) is var (prevKey, proof) && prevKey is not null)
            {
                req.PreviousSshPublicKey = prevKey;
                req.PreviousKeyProof = proof;
            }
            var (resp, status) = await RequestCertAsync(req, ct);
            if (resp is null)
            {
                // Only the server's own refusals say it did not take the key: a bad request, an unknown or
                // unapproved device, a missing endpoint, a revoked key. A 5xx or a lost answer says nothing.
                if (status is 400 or 401 or 403 or 404 or 409) { DeleteStaged(state); DeleteState(); }
                if (status == 404) return true;
                if (status > 0) logger.LogWarning(L.SshCertService_ServerRefused, status);
                return false;
            }
            WriteAtomic(NextCert(state), resp.Certificate.Trim() + "\n");
            state.Stage = "issued";
            state.ValidBefore = resp.ValidBefore;
            state.PendingConfirm = resp.Rotated && resp.ConfirmWithinMinutes is > 0;
            WriteState(state);
        }

        if (state.Stage != "issued") return true;
        if (!state.PendingConfirm)
        {
            // An older server adopted the key and revoked the previous one on the request itself.
            Finalize(state, current);
            return true;
        }

        // Prove the key where it matters before anything is revoked: a real connection to the bastion.
        var staged = StagedSnapshot(state);
        switch (await ProbeAsync(staged, ct))
        {
            case Probe.Ok:
                break;
            case Probe.Refused:
                logger.LogWarning(L.SshCertService_ProbeFailed, state.Provider);
                DeleteStaged(state);
                WriteState(new RotationState { Stage = "failed", FailedUtc = DateTimeOffset.UtcNow, Provider = state.Provider });
                return true;
            default:
                logger.LogInformation(L.SshCertService_ProbeInconclusive);
                return false;
        }

        var confirm = await ConfirmAsync(state.PublicKeyLine, ct);
        if (confirm == 0) { Finalize(state, current); return true; }
        if (confirm == 409) { state.Stage = "requested"; WriteState(state); return false; } // the offer lapsed: ask again
        if (confirm is >= 400 and < 500) { logger.LogWarning(L.SshCertService_ConfirmRefused, confirm); DeleteStaged(state); DeleteState(); return false; }
        return false; // 5xx or no answer: the offer may still stand, try again soon
    }

    /// <summary>The switch: files into place, the live identity, enrollment.json, the old key gone.</summary>
    private void Finalize(RotationState state, SshIdentity.Snapshot previous)
    {
        SshIdentity.Snapshot next;
        if (state.KeyName is not null)
        {
            File.Move(NextPub(state), CngPubPath, overwrite: true);
            File.Move(NextCert(state), CngCertPath, overwrite: true);
            next = new SshIdentity.Snapshot(state.Provider, state.KeyName, CngPubPath, CngCertPath, null);
        }
        else
        {
            File.Move(FileKeyPath + ".next", FileKeyPath, overwrite: true);
            File.Move(FileKeyPath + ".next.pub", FileKeyPath + ".pub", overwrite: true);
            File.Move(FileKeyPath + ".next-cert.pub", FileKeyPath + "-cert.pub", overwrite: true);
            next = new SshIdentity.Snapshot(DeviceKeyStore.File, null, FileKeyPath + ".pub", FileKeyPath + "-cert.pub", FileKeyPath);
        }
        SshIdentity.Set(next);
        RewriteEnrollment(next);
        DeleteState();
        _rotateRequested = false;

        // The previous key material, best effort: a failure here must not undo the switch.
        try
        {
            if (previous.InAgent && previous.KeyName != next.KeyName) SshKeyStore.Delete(previous.KeyName);
            if (!previous.InAgent && next.InAgent)
                foreach (var p in new[] { FileKeyPath, FileKeyPath + ".pub", FileKeyPath + "-cert.pub" }) if (File.Exists(p)) File.Delete(p);
            if (previous.InAgent && !next.InAgent)
                foreach (var p in new[] { CngPubPath, CngCertPath }) if (File.Exists(p)) File.Delete(p);
        }
        catch (Exception ex) { logger.LogWarning(ex, L.RekeyService_OldKeyCleanupFailed); }

        if (next.InAgent && !previous.InAgent) logger.LogWarning(L.SshCertService_MovedToTpm, next.Provider, state.ValidBefore ?? DateTimeOffset.UtcNow);
        else logger.LogWarning(L.SshCertService_Rotated, state.ValidBefore ?? DateTimeOffset.UtcNow);
    }

    private enum Probe { Ok, Refused, Inconclusive }

    /// <summary>A real reverse connection with the staged key (a throwaway forward, -R 0). Refused = the bastion
    /// reachable but the key not accepted; inconclusive = the bastion not reachable at all.</summary>
    private async Task<Probe> ProbeAsync(SshIdentity.Snapshot staged, CancellationToken ct)
    {
        if (!await BastionReachableAsync(ct)) return Probe.Inconclusive;
        SshIdentity.Staged = staged; // the agent serves it for the probe
        try
        {
            await using var probe = new SshReverseTunnel(_opt.Tunnel, transport, loggerFactory.CreateLogger<SshReverseTunnel>()) { IdentityOverride = staged };
            await probe.StartAsync(0, 0, ct);
            var ok = probe.IsRunning;
            await probe.StopAsync();
            return ok ? Probe.Ok : Probe.Refused;
        }
        finally { SshIdentity.Staged = null; }
    }

    private async Task<bool> BastionReachableAsync(CancellationToken ct)
    {
        foreach (var port in new[] { 443, _opt.Tunnel.BastionPort }.Distinct())
        {
            try
            {
                using var c = new TcpClient();
                using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                t.CancelAfter(TimeSpan.FromSeconds(5));
                await c.ConnectAsync(_opt.Tunnel.BastionHost, port, t.Token);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { }
        }
        return false;
    }

    /// <summary>The key in use until now and the proof of holding it (a signature with it over the new key's body):
    /// ssh-keygen for a key file, our own SSHSIG for a CNG key.</summary>
    private (string? Key, string? Proof) Previous(SshIdentity.Snapshot current, string newBody)
    {
        string? prev = null;
        try { prev = File.Exists(current.PublicKeyPath) ? File.ReadAllText(current.PublicKeyPath).Trim() : null; } catch (IOException) { }
        if (prev is null) return (null, null);
        var message = Encoding.UTF8.GetBytes(newBody + "\n");
        if (current.InAgent)
        {
            try
            {
                using var key = SshKeyStore.Open(current.KeyName);
                if (key is null) return (prev, null);
                var proof = SshKeyEncoding.SshSig(SshKeyStore.PublicKeyBlob(key), "remoteagent-rotation", message, toSign => SshKeyStore.SignRs(key, toSign));
                return (prev, proof);
            }
            catch (CryptographicException) { return (prev, null); }
        }
        return (prev, current.PrivateKeyPath is { } kp ? SignWithKeyFile(kp, newBody) : null);
    }

    // ---- server calls --------------------------------------------------------------------------------

    private async Task<(SshCertResponse? Resp, int Status)> RequestCertAsync(SshCertRequest req, CancellationToken ct)
    {
        using var http = BuildClient();
        using var r = await http.PostAsJsonAsync($"{BaseUrl}/api/ssh-cert", req, AgentJsonContext.Default.SshCertRequest, ct);
        if (!r.IsSuccessStatusCode) return (null, (int)r.StatusCode);
        var resp = await r.Content.ReadFromJsonAsync(AgentJsonContext.Default.SshCertResponse, ct);
        if (resp is null || string.IsNullOrWhiteSpace(resp.Certificate) || SshCertificate.Parse(resp.Certificate) is null) return (null, 0);
        return (resp, 200);
    }

    /// <summary>0 on success, else the status (0 also for a transport failure is avoided: -1).</summary>
    private async Task<int> ConfirmAsync(string pub, CancellationToken ct)
    {
        try
        {
            using var http = BuildClient();
            using var r = await http.PostAsJsonAsync($"{BaseUrl}/api/ssh-cert/confirm", new SshCertConfirm { SshPublicKey = pub }, AgentJsonContext.Default.SshCertConfirm, ct);
            return r.IsSuccessStatusCode ? 0 : (int)r.StatusCode;
        }
        catch (HttpRequestException) { return -1; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return -1; }
    }

    // ---- state and files -----------------------------------------------------------------------------

    private string NextPub(RotationState s) => s.KeyName is not null ? Path.Combine(Dir, "id_ecdsa.next.pub") : FileKeyPath + ".next.pub";
    private string NextCert(RotationState s) => s.KeyName is not null ? Path.Combine(Dir, "id_ecdsa.next-cert.pub") : FileKeyPath + ".next-cert.pub";

    private SshIdentity.Snapshot StagedSnapshot(RotationState s) => s.KeyName is not null
        ? new SshIdentity.Snapshot(s.Provider, s.KeyName, NextPub(s), NextCert(s), null)
        : new SshIdentity.Snapshot(DeviceKeyStore.File, null, NextPub(s), NextCert(s), FileKeyPath + ".next");

    private void DeleteStaged(RotationState s)
    {
        if (s.KeyName is not null) SshKeyStore.Delete(s.KeyName);
        foreach (var p in new[] { NextPub(s), NextCert(s), FileKeyPath + ".next" })
            try { File.Delete(p); } catch { /* best effort */ }
    }

    private RotationState? ReadState()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize(File.ReadAllText(StatePath), AgentLocalJsonContext.Default.RotationState) : null; }
        catch (Exception) { return null; }
    }

    private void WriteState(RotationState s) => WriteAtomic(StatePath, JsonSerializer.Serialize(s, AgentLocalJsonContext.Default.RotationState));
    private void DeleteState() { try { File.Delete(StatePath); } catch { /* best effort */ } }

    private static SshCertificate.Validity? ReadCert(string path)
    {
        try { return File.Exists(path) ? SshCertificate.Parse(File.ReadAllText(path)) : null; }
        catch (IOException) { return null; }
    }

    private static bool CngUsable()
    {
        try { using var k = SshKeyStore.Create(preferTpm: DeviceKeyStore.TpmUsable()); SshKeyStore.Delete(k.KeyName); return true; }
        catch (CryptographicException) { return false; }
    }

    private void RewriteEnrollment(SshIdentity.Snapshot s)
    {
        var path = Path.Combine(Dir, "enrollment.json");
        var rec = JsonSerializer.Deserialize(File.ReadAllText(path), AgentLocalJsonContext.Default.EnrollmentRecord)!;
        rec.SshKeyProvider = s.Provider;
        rec.SshKeyName = s.KeyName;
        WriteAtomic(path, JsonSerializer.Serialize(rec, AgentLocalJsonContext.Default.EnrollmentRecord));
    }

    private static string GenerateKeyFile(string keyPath)
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

    /// <summary>ssh-keygen -Y sign with a key file, namespace "remoteagent-rotation", over the message plus a
    /// newline. Null when the installed ssh-keygen cannot sign (older builds): then the previous key simply is not
    /// proven, as before.</summary>
    private static string? SignWithKeyFile(string keyPath, string message)
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
