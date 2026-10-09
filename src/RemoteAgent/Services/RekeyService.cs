using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
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
/// Moves the device key into the TPM and renews the certificate before it ends; both are "new key, new
/// certificate, old one retired". Checked once after start (after a randomized 5-30 minute delay, so a fleet-wide
/// agent rollout does not re-key every device in the same minute) and then daily.
///
/// Order of operations, so a failure anywhere leaves the device on its old identity: new key → CSR → the server
/// issues a certificate but keeps the old one primary → the new certificate is installed and proven with a
/// confirm call made with it → only then the agent switches, rewrites enrollment.json and deletes the old key
/// material. The server retires the old certificate at the confirm, with a short grace for connections in flight.
/// </summary>
public sealed class RekeyService(IOptions<AgentOptions> options, ReconnectSignal reconnect, ILogger<RekeyService> logger) : BackgroundService
{
    private readonly AgentOptions _opt = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (DeviceIdentity.Current is null || string.IsNullOrWhiteSpace(_opt.Telemetry.IngestUrl)) return;

        // No usable key (the TPM was cleared, the store entry is gone): nothing else in this agent can talk to
        // the server. Ask for a new certificate and wait for an administrator; this takes over until it is done.
        if (!IdentityUsable(DeviceIdentity.Current))
        {
            try { await RecoverAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        try { await Task.Delay(TimeSpan.FromMinutes(5 + Random.Shared.Next(0, 26)), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, L.RekeyService_Failed); }
            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var id = DeviceIdentity.Current;
        if (id is null) return;
        var reason = RekeyPolicy.Reason(id.Provider, DeviceKeyStore.TpmUsable(), id.NotAfter, DateTimeOffset.UtcNow);
        if (reason is null) return;
        await RekeyAsync(id, reason, ct);
    }

    /// <summary>One re-key round; public so a console command can trigger it later.</summary>
    public async Task<bool> RekeyAsync(DeviceIdentity.Snapshot id, string reason, CancellationToken ct)
    {
        var baseUrl = _opt.Telemetry.IngestUrl.Replace("/api/telemetry", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        using var fresh = DeviceKeyStore.Create(preferTpm: DeviceKeyStore.TpmUsable());
        string? newThumb = null;
        bool switched = false, confirmSent = false;
        try
        {
            var csr = new CertificateRequest("CN=rekey", fresh.Key, HashAlgorithmName.SHA256).CreateSigningRequestPem();

            RekeyResponse? resp;
            using (var http = BuildClient(id))
            {
                using var r = await http.PostAsJsonAsync($"{baseUrl}/api/rekey",
                    new RekeyRequest { Csr = csr, KeyProvider = fresh.Provider, Reason = reason }, AgentJsonContext.Default.RekeyRequest, ct);
                if (!r.IsSuccessStatusCode)
                {
                    logger.LogWarning(L.RekeyService_ServerRefused, (int)r.StatusCode);
                    return false;
                }
                resp = await r.Content.ReadFromJsonAsync(AgentJsonContext.Default.RekeyResponse, ct);
            }
            if (resp is null || string.IsNullOrEmpty(resp.Certificate)) return false;

            newThumb = DeviceKeyStore.InstallCertificate(resp.Certificate, fresh.Key);
            var candidate = new DeviceIdentity.Snapshot(fresh.Provider, newThumb, fresh.KeyName, null, resp.NotAfter);

            // Prove the new certificate at the server before anything old is touched. Once the confirm request
            // has left, the server may have committed even if the answer never arrives: from here the candidate
            // is kept unless the server says, in so many words, that it is not the certificate of record.
            confirmSent = true;
            var confirmed = await ConfirmAsync(candidate, baseUrl, ct);
            if (confirmed is null)
            {
                // Lost answer: ask once more. A commit shows as "nothing pending" with this certificate accepted.
                confirmed = await ConfirmAsync(candidate, baseUrl, ct);
                if (confirmed is null)
                {
                    logger.LogWarning(L.RekeyService_ConfirmUnknown, newThumb);
                    return false; // candidate kept (see finally); the next round resolves it
                }
            }
            if (confirmed == false) { confirmSent = false; return false; } // the server refused it: safe to discard

            // Switch first: every later connection uses the new identity and enrollment.json says so for the next
            // start. The old key material goes afterwards, best effort - a failure there must not undo the switch.
            DeviceIdentity.Set(candidate);
            RewriteEnrollment(candidate);
            switched = true;
            try
            {
                if (id.InStore) { DeviceKeyStore.RemoveCertificate(id.Thumbprint); DeviceKeyStore.DeleteKey(id.KeyName); }
                else if (id.PfxPath is { } pfx) System.IO.File.Delete(pfx);
            }
            catch (Exception ex) { logger.LogWarning(ex, L.RekeyService_OldKeyCleanupFailed); }
            logger.LogWarning(L.RekeyService_Rekeyed, reason, fresh.Provider, resp.NotAfter);
            reconnect.Request(); // the command channel moves to the new certificate now, not at its next drop
            return true;
        }
        finally
        {
            // Refused, empty, or thrown before the confirm left: the key and certificate made for this round go,
            // or every daily retry would leave another machine key behind in the TPM. After the confirm left they
            // stay: the server may trust them now, and deleting them would cost an administrator-approved recovery.
            if (!switched && !confirmSent)
            {
                if (newThumb is not null) DeviceKeyStore.RemoveCertificate(newThumb);
                DeviceKeyStore.DeleteKey(fresh.KeyName);
            }
        }
    }

    /// <summary>One confirm call with the candidate's certificate. True: the server made it the certificate of
    /// record (now, or already - "nothing pending" while accepting this certificate). False: the server refused it
    /// (not the pending one, or the offer lapsed). Null: no answer arrived, so nothing is known.</summary>
    private async Task<bool?> ConfirmAsync(DeviceIdentity.Snapshot candidate, string baseUrl, CancellationToken ct)
    {
        try
        {
            using var http = BuildClient(candidate);
            using var r = await http.PostAsync($"{baseUrl}/api/rekey/confirm", content: null, ct);
            if (r.IsSuccessStatusCode) return true;
            if ((int)r.StatusCode == 409)
            {
                // Authenticated with the candidate yet nothing pending: the earlier confirm committed.
                try { var e = await r.Content.ReadFromJsonAsync(AgentJsonContext.Default.RekeyError, ct); if (e?.Code == "nothing_pending") return true; }
                catch { /* no body */ }
            }
            logger.LogWarning(L.RekeyService_ConfirmFailed, (int)r.StatusCode);
            return false;
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; } // timeout
    }

    // ---- lost key -------------------------------------------------------------------------------------

    private static bool IdentityUsable(DeviceIdentity.Snapshot id)
    {
        if (!id.InStore) return true; // a PFX file: present or not, the old path handles it
        if (!DeviceKeyStore.KeyExists(id.KeyName)) return false;
        try { using var c = CertHelper.ResolveClientCertificate(null, id.Thumbprint); return c.HasPrivateKey; }
        catch { return false; }
    }

    public sealed class RecoveryState
    {
        public Guid RequestId { get; set; }
        public string Token { get; set; } = "";
        public string KeyName { get; set; } = "";
        public string Provider { get; set; } = "";
        public DateTimeOffset OpenedUtc { get; set; }
        public string? KeyFingerprint { get; set; }
    }

    /// <summary>SHA-256 of the key's SubjectPublicKeyInfo, shortened like the console shows it.</summary>
    public static string KeyFingerprint(ECDsa key) =>
        Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16];

    private string RecoveryPath => Path.Combine(_opt.EnrollmentDir, "rekey-request.json");

    /// <summary>Opens (or resumes) a lost-key request and polls it until the administrator decides. On approval the
    /// device switches to the new certificate and returns; on rejection or expiry it waits a day and asks again.</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        var id = DeviceIdentity.Current!;
        var baseUrl = _opt.Telemetry.IngestUrl.Replace("/api/telemetry", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        var rec = JsonSerializer.Deserialize(System.IO.File.ReadAllText(Path.Combine(_opt.EnrollmentDir, "enrollment.json")), AgentLocalJsonContext.Default.EnrollmentRecord)!;
        logger.LogError(L.RekeyService_KeyLost, id.Provider);

        while (!ct.IsCancellationRequested)
        {
            RecoveryState? state = null;
            try { if (System.IO.File.Exists(RecoveryPath)) state = JsonSerializer.Deserialize(System.IO.File.ReadAllText(RecoveryPath), AgentLocalJsonContext.Default.RecoveryState); }
            catch { state = null; }
            ECDsa? key = state is null ? null : DeviceKeyStore.Open(state.KeyName);
            if (state is null || key is null)
            {
                // A fresh key and a fresh request. The key is persisted under its name, so a restart resumes
                // with the same request rather than opening a new one every time.
                var fresh = DeviceKeyStore.Create(preferTpm: DeviceKeyStore.TpmUsable());
                key = fresh.Key;
                var csr = new CertificateRequest("CN=rekey", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();
                var fingerprint = KeyFingerprint(key);
                using var http = PlainClient();
                using var r = await http.PostAsJsonAsync($"{baseUrl}/enroll/rekey",
                    new RekeyRequestOpen { DeviceId = rec.DeviceId, Hostname = Environment.MachineName, Csr = csr, KeyProvider = fresh.Provider },
                    AgentJsonContext.Default.RekeyRequestOpen, ct);
                if (!r.IsSuccessStatusCode)
                {
                    // 409: the server already holds a request for this device, or still sees it alive - wait, do not
                    // pile up keys; anything else: try again later all the same.
                    logger.LogWarning(L.RekeyService_RequestFailed, (int)r.StatusCode);
                    key.Dispose();
                    DeviceKeyStore.DeleteKey(fresh.KeyName);
                    await Task.Delay(TimeSpan.FromMinutes(15), ct);
                    continue;
                }
                var opened = (await r.Content.ReadFromJsonAsync(AgentJsonContext.Default.RekeyRequestOpened, ct))!;
                state = new RecoveryState { RequestId = opened.RequestId, Token = opened.Token, KeyName = fresh.KeyName, Provider = fresh.Provider, OpenedUtc = DateTimeOffset.UtcNow, KeyFingerprint = fingerprint };
                System.IO.File.WriteAllText(RecoveryPath, JsonSerializer.Serialize(state, AgentLocalJsonContext.Default.RecoveryState));
                // The fingerprint is what the administrator sees in the console next to the request: it is in this
                // log and in the event log so the two can be compared before approving.
                logger.LogWarning(L.RekeyService_RequestOpened, opened.RequestId, fingerprint);
            }

            // Poll until decided.
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                RekeyRequestStatus? status; int code;
                try
                {
                    using var http = PlainClient();
                    using var r = await http.GetAsync($"{baseUrl}/enroll/rekey/{state.RequestId}?token={Uri.EscapeDataString(state.Token)}", ct);
                    code = (int)r.StatusCode;
                    status = code is 200 or 202 or 410 ? await r.Content.ReadFromJsonAsync(AgentJsonContext.Default.RekeyRequestStatus, ct) : null;
                }
                catch (HttpRequestException) { continue; } // network: keep polling
                if (code == 202) continue;
                if (code == 200 && status?.Certificate is { } pem)
                {
                    var thumb = DeviceKeyStore.InstallCertificate(pem, key);
                    var snapshot = new DeviceIdentity.Snapshot(state.Provider, thumb, state.KeyName, null, status.NotAfter);
                    DeviceIdentity.Set(snapshot);
                    RewriteEnrollment(snapshot);
                    try { System.IO.File.Delete(RecoveryPath); } catch { /* best effort */ }
                    DeviceKeyStore.RemoveCertificate(id.Thumbprint);
                    logger.LogWarning(L.RekeyService_Recovered, state.Provider, status.NotAfter);
                    key.Dispose();
                    reconnect.Request(); // the channel has been failing without a certificate; try at once
                    return;
                }
                // Rejected, expired, or the server no longer knows the request: start over tomorrow.
                logger.LogError(L.RekeyService_RequestRejected, status?.State ?? code.ToString());
                try { System.IO.File.Delete(RecoveryPath); } catch { /* best effort */ }
                DeviceKeyStore.DeleteKey(state.KeyName);
                key.Dispose();
                await Task.Delay(TimeSpan.FromHours(24), ct);
                break;
            }
        }
    }

    /// <summary>A client with no certificate (the device has none to offer), still pinned when a pin is set.</summary>
    private HttpClient PlainClient()
    {
        var handler = new SocketsHttpHandler();
        if (!string.IsNullOrWhiteSpace(_opt.Telemetry.ServerCertPinSha256))
            handler.SslOptions.RemoteCertificateValidationCallback = CertHelper.PinnedServerValidator(_opt.Telemetry.ServerCertPinSha256);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private void RewriteEnrollment(DeviceIdentity.Snapshot s)
    {
        var path = Path.Combine(_opt.EnrollmentDir, "enrollment.json");
        var rec = JsonSerializer.Deserialize(System.IO.File.ReadAllText(path), AgentLocalJsonContext.Default.EnrollmentRecord)!;
        rec.CertThumbprint = s.Thumbprint;
        rec.KeyProvider = s.Provider;
        rec.KeyName = s.KeyName;
        rec.CertNotAfterUtc = s.NotAfter;
        var tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(rec, AgentLocalJsonContext.Default.EnrollmentRecord));
        System.IO.File.Move(tmp, path, overwrite: true);
    }

    /// <summary>A client presenting exactly this snapshot's certificate - not the live identity, which is still
    /// the old one while a candidate proves itself at /api/rekey/confirm.</summary>
    private HttpClient BuildClient(DeviceIdentity.Snapshot id)
    {
        var handler = new SocketsHttpHandler();
        handler.SslOptions.ClientCertificates ??= new();
        handler.SslOptions.ClientCertificates.Add(id.InStore
            ? CertHelper.LoadClientCertificate(id.Thumbprint)
            : CertHelper.LoadClientCertificateFromProtectedPfx(id.PfxPath!));
        if (!string.IsNullOrWhiteSpace(_opt.Telemetry.ServerCertPinSha256))
            handler.SslOptions.RemoteCertificateValidationCallback = CertHelper.PinnedServerValidator(_opt.Telemetry.ServerCertPinSha256);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }
}
