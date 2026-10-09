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
/// agent rollout does not re-key every device in the same minute) and then daily; the console can ask for a
/// round at any time. Rounds are serialized: one at a time, whoever asks.
///
/// Order of operations, so a failure anywhere leaves the device on its old identity: new key → CSR → the server
/// issues a certificate but keeps the old one primary → the new certificate is installed and proven with a
/// confirm call made with it → only then the agent switches, rewrites enrollment.json and deletes the old key
/// material. A candidate whose confirmation got no answer is written to rekey-pending.json and resolved before
/// anything else, in the next round or after a restart: the server may already trust it.
///
/// A device whose key is gone (the TPM was cleared) has nothing to authenticate with; see <see cref="RecoverAsync"/>.
/// </summary>
public sealed class RekeyService(IOptions<AgentOptions> options, ReconnectSignal reconnect, ILogger<RekeyService> logger) : BackgroundService
{
    private readonly AgentOptions _opt = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string BaseUrl => _opt.Telemetry.IngestUrl.Replace("/api/telemetry", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
    private string PendingPath => Path.Combine(_opt.EnrollmentDir, "rekey-pending.json");
    private string RecoveryPath => Path.Combine(_opt.EnrollmentDir, "rekey-request.json");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (DeviceIdentity.Current is null || string.IsNullOrWhiteSpace(_opt.Telemetry.IngestUrl)) return;

        // Nothing on this path may end the service: with the host's default behaviour an unhandled exception
        // here would stop the whole agent, channel and tunnels included, and a bad file would do it every start.
        try
        {
            // A candidate from an earlier round whose confirmation got no answer comes first: the server may
            // already trust it, and a working identity must not be thrown away for a new attempt.
            await RunSerializedAsync(async ct => { await ResolvePendingAsync(ct); }, stoppingToken);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { logger.LogWarning(ex, L.RekeyService_Failed); }
        try
        {
            // No usable key (the TPM was cleared, the store entry is gone): nothing else in this agent can talk to
            // the server. Ask for a new certificate and wait for an administrator; this takes over until it is done.
            if (DeviceIdentity.Current is { } id && !IdentityUsable(id))
                await RunSerializedAsync(RecoverAsync, stoppingToken);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { logger.LogWarning(ex, L.RekeyService_Failed); }
        try { await Task.Delay(TimeSpan.FromMinutes(5 + Random.Shared.Next(0, 26)), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunSerializedAsync(CheckAsync, stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, L.RekeyService_Failed); }
            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunSerializedAsync(Func<CancellationToken, Task> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await work(ct); }
        finally { _gate.Release(); }
    }

    /// <summary>The daily decision. A pending candidate or a lost key is dealt with before the policy is asked.</summary>
    private async Task CheckAsync(CancellationToken ct)
    {
        if (await ResolvePendingAsync(ct)) return; // a candidate is still in flight: nothing new this round
        var id = DeviceIdentity.Current;
        if (id is null) return;
        if (!IdentityUsable(id)) { await RecoverAsync(ct); return; }
        var reason = RekeyPolicy.Reason(id.Provider, DeviceKeyStore.TpmUsable(), id.NotAfter, DateTimeOffset.UtcNow);
        if (reason is null) return;
        await RekeyCoreAsync(id, reason, ct);
    }

    /// <summary>One re-key round on request (the console's "new device key"). Serialized with the daily check.</summary>
    public async Task<bool> RekeyAsync(DeviceIdentity.Snapshot id, string reason, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (await ResolvePendingAsync(ct)) return false; // an earlier candidate is still unresolved: no second one
            return await RekeyCoreAsync(DeviceIdentity.Current ?? id, reason, ct);
        }
        finally { _gate.Release(); }
    }

    // ---- the round ------------------------------------------------------------------------------------

    /// <summary>A candidate certificate installed and waiting for its confirmation to be known.</summary>
    public sealed class PendingCandidate
    {
        public string KeyName { get; set; } = "";
        public string Provider { get; set; } = "";
        public string Thumbprint { get; set; } = "";
        public DateTimeOffset? NotAfter { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
    }

    private async Task<bool> RekeyCoreAsync(DeviceIdentity.Snapshot id, string reason, CancellationToken ct)
    {
        using var fresh = DeviceKeyStore.Create(preferTpm: DeviceKeyStore.TpmUsable());
        string? newThumb = null;
        bool keep = false; // once the candidate is on file, this round no longer owns its cleanup
        try
        {
            var csr = new CertificateRequest("CN=rekey", fresh.Key, HashAlgorithmName.SHA256).CreateSigningRequestPem();

            RekeyResponse? resp;
            using (var http = BuildClient(id))
            {
                using var r = await http.PostAsJsonAsync($"{BaseUrl}/api/rekey",
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
            var candidate = new PendingCandidate { KeyName = fresh.KeyName, Provider = fresh.Provider, Thumbprint = newThumb, NotAfter = resp.NotAfter, CreatedUtc = DateTimeOffset.UtcNow };
            // On file before the confirm leaves: from here the server may trust the candidate at any moment, so
            // it must survive a crash or a lost answer and be resolved later rather than remade.
            WriteJson(PendingPath, JsonSerializer.Serialize(candidate, AgentLocalJsonContext.Default.PendingCandidate));
            keep = true;

            return await ResolveCandidateAsync(candidate, id, reason, ct) == true;
        }
        finally
        {
            // Refused, empty, or thrown before the candidate was on file: the key and certificate made for this
            // round go, or every daily retry would leave another machine key behind in the TPM.
            if (!keep)
            {
                if (newThumb is not null) DeviceKeyStore.RemoveCertificate(newThumb);
                DeviceKeyStore.DeleteKey(fresh.KeyName);
            }
        }
    }

    /// <summary>Confirms a candidate at the server and acts on the outcome. True: switched to it. False: the server
    /// refused it, and it is gone. Null: still unknown (no answer twice), kept on file for a later round.</summary>
    private async Task<bool?> ResolveCandidateAsync(PendingCandidate candidate, DeviceIdentity.Snapshot current, string reason, CancellationToken ct)
    {
        var snapshot = new DeviceIdentity.Snapshot(candidate.Provider, candidate.Thumbprint, candidate.KeyName, null, candidate.NotAfter);
        var confirmed = await ConfirmAsync(snapshot, ct) ?? await ConfirmAsync(snapshot, ct);
        if (confirmed is null)
        {
            logger.LogWarning(L.RekeyService_ConfirmUnknown, candidate.Thumbprint);
            return null;
        }
        if (confirmed == false)
        {
            DeviceKeyStore.RemoveCertificate(candidate.Thumbprint);
            DeviceKeyStore.DeleteKey(candidate.KeyName);
            try { File.Delete(PendingPath); } catch { /* best effort */ }
            return false;
        }

        // Switch first: every later connection uses the new identity and enrollment.json says so for the next
        // start. The old key material goes afterwards, best effort - a failure there must not undo the switch.
        DeviceIdentity.Set(snapshot);
        RewriteEnrollment(snapshot);
        try { File.Delete(PendingPath); } catch { /* best effort */ }
        try
        {
            if (current.Thumbprint != candidate.Thumbprint)
            {
                if (current.InStore) { DeviceKeyStore.RemoveCertificate(current.Thumbprint); DeviceKeyStore.DeleteKey(current.KeyName); }
                else if (current.PfxPath is { } pfx) File.Delete(pfx);
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, L.RekeyService_OldKeyCleanupFailed); }
        logger.LogWarning(L.RekeyService_Rekeyed, reason, candidate.Provider, candidate.NotAfter);
        reconnect.Request(); // the command channel moves to the new certificate now, not at its next drop
        return true;
    }

    /// <summary>Resolves a candidate left on file by an earlier round. True while one is still unresolved.</summary>
    private async Task<bool> ResolvePendingAsync(CancellationToken ct)
    {
        PendingCandidate? pending = null;
        try { if (File.Exists(PendingPath)) pending = JsonSerializer.Deserialize(File.ReadAllText(PendingPath), AgentLocalJsonContext.Default.PendingCandidate); }
        catch { /* unreadable: treated as none */ }
        if (pending is null || DeviceIdentity.Current is not { } current) return false;
        if (current.Thumbprint == pending.Thumbprint)
        {
            // Already the live identity: the switch happened, but the round may have died before enrollment.json
            // was rewritten (the pending file is still here). Persist it again - idempotent - so a restart does not
            // come back with the old certificate, then drop the file.
            RewriteEnrollment(current);
            try { File.Delete(PendingPath); } catch { /* best effort */ }
            return false;
        }
        if (!DeviceKeyStore.KeyExists(pending.KeyName))
        {
            try { File.Delete(PendingPath); } catch { /* best effort */ }   // nothing left to resolve
            return false;
        }
        var outcome = await ResolveCandidateAsync(pending, current, "pending", ct);
        return outcome is null;
    }

    /// <summary>One confirm call with the candidate's certificate. True: the server made it the certificate of
    /// record (now, or already - "nothing pending" while accepting this certificate). False: the server refused it
    /// (not the pending one, or the offer lapsed). Null: no answer arrived, so nothing is known.</summary>
    private async Task<bool?> ConfirmAsync(DeviceIdentity.Snapshot candidate, CancellationToken ct)
    {
        HttpClient http;
        try { http = BuildClient(candidate); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The candidate is not usable on this machine (its certificate is gone from the store): no point
            // keeping it, whatever the server thinks.
            logger.LogWarning(ex, L.RekeyService_ConfirmFailed, 0);
            return false;
        }
        try
        {
            using (http)
            {
                using var r = await http.PostAsync($"{BaseUrl}/api/rekey/confirm", content: null, ct);
                if (r.IsSuccessStatusCode) return true;
                var sc = (int)r.StatusCode;
                if (sc == 409)
                {
                    // Authenticated with the candidate yet nothing pending: the earlier confirm committed.
                    try { var e = await r.Content.ReadFromJsonAsync(AgentJsonContext.Default.RekeyError, ct); if (e?.Code == "nothing_pending") return true; }
                    catch { /* no body */ }
                }
                // A proxy or server hiccup says nothing about whether the commit happened: unknown, try later.
                if (sc == 429 || sc >= 500) return null;
                logger.LogWarning(L.RekeyService_ConfirmFailed, sc);
                return false; // an explicit refusal (not the pending one, offer lapsed, not authorized)
            }
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; } // timeout
    }

    // ---- lost key -------------------------------------------------------------------------------------

    private static bool IdentityUsable(DeviceIdentity.Snapshot id)
    {
        if (!id.InStore) return true; // a PFX file: present or not, the old path handles it
        if (!DeviceKeyStore.KeyExists(id.KeyName)) return false;
        try { using var c = CertHelper.LoadClientCertificate(id.Thumbprint); return c.HasPrivateKey; }
        catch { return false; }
    }

    public sealed class RecoveryState
    {
        /// <summary>Empty until the server has accepted the request: the key exists, the request may not.</summary>
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

    /// <summary>
    /// Opens (or resumes) a lost-key request and polls it until the administrator decides. The key is made and
    /// written to rekey-request.json before the request is sent, so a crash in between does not strand a request
    /// the server accepted: sending the same key again makes the server hand back that request with fresh polling
    /// credentials (the CSR proves the key is ours). On approval the device switches to the new certificate and
    /// returns; on rejection or expiry it waits a day and asks again. Transport trouble is waited out, never fatal.
    /// </summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        var id = DeviceIdentity.Current!;
        logger.LogError(L.RekeyService_KeyLost, id.Provider);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var rec = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(_opt.EnrollmentDir, "enrollment.json")), AgentLocalJsonContext.Default.EnrollmentRecord)!;
                RecoveryState? state = null;
                try { if (File.Exists(RecoveryPath)) state = JsonSerializer.Deserialize(File.ReadAllText(RecoveryPath), AgentLocalJsonContext.Default.RecoveryState); }
                catch { state = null; }
                ECDsa? key = state is null ? null : DeviceKeyStore.Open(state.KeyName);
                if (state is null || key is null)
                {
                    var fresh = DeviceKeyStore.Create(preferTpm: DeviceKeyStore.TpmUsable());
                    key = fresh.Key;
                    state = new RecoveryState { KeyName = fresh.KeyName, Provider = fresh.Provider, KeyFingerprint = KeyFingerprint(key) };
                    WriteJson(RecoveryPath, JsonSerializer.Serialize(state, AgentLocalJsonContext.Default.RecoveryState));
                }
                using (key)
                {
                    if (state.RequestId == Guid.Empty && !await OpenRequestAsync(state, key, rec.DeviceId, ct))
                    {
                        await Task.Delay(TimeSpan.FromMinutes(15), ct);
                        continue;
                    }
                    var outcome = await PollRequestAsync(state, key, id, ct);
                    if (outcome == PollOutcome.Recovered) return;
                    if (outcome == PollOutcome.Reopen) continue; // the server lost the request: ask again with the same key, now
                }
                await Task.Delay(TimeSpan.FromHours(24), ct); // rejected or expired: a new request tomorrow
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, L.RekeyService_Failed);
                await Task.Delay(TimeSpan.FromMinutes(15), ct);
            }
        }
    }

    /// <summary>Sends the request for the key on file. True when the server accepted it (id and token now in the
    /// state). A pending request for this very key comes back as the same request with new credentials.</summary>
    private async Task<bool> OpenRequestAsync(RecoveryState state, ECDsa key, string deviceId, CancellationToken ct)
    {
        var csr = new CertificateRequest("CN=rekey", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        HttpResponseMessage r;
        try
        {
            using var http = PlainClient();
            r = await http.PostAsJsonAsync($"{BaseUrl}/enroll/rekey",
                new RekeyRequestOpen { DeviceId = deviceId, Hostname = Environment.MachineName, Csr = csr, KeyProvider = state.Provider },
                AgentJsonContext.Default.RekeyRequestOpen, ct);
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return false; }
        using (r)
        {
            if (!r.IsSuccessStatusCode)
            {
                // 409: the server still sees the device alive, or holds somebody else's request; 404: the device is
                // gone from the server. The key stays for the next try, nothing piles up.
                logger.LogWarning(L.RekeyService_RequestFailed, (int)r.StatusCode);
                return false;
            }
            var opened = await r.Content.ReadFromJsonAsync(AgentJsonContext.Default.RekeyRequestOpened, ct);
            if (opened is null || opened.RequestId == Guid.Empty) return false;
            state.RequestId = opened.RequestId; state.Token = opened.Token; state.OpenedUtc = DateTimeOffset.UtcNow;
            WriteJson(RecoveryPath, JsonSerializer.Serialize(state, AgentLocalJsonContext.Default.RecoveryState));
            // The fingerprint is what the administrator sees in the console next to the request: it is in this
            // log and in the event log so the two can be compared before approving.
            logger.LogWarning(L.RekeyService_RequestOpened, opened.RequestId, state.KeyFingerprint);
            return true;
        }
    }

    private enum PollOutcome { Recovered, Ended, Reopen }

    /// <summary>Polls the request every minute until the administrator decides. Recovered: the device is back on a
    /// new certificate. Ended: rejected or expired (state and key gone; a new request is due later). Reopen: the
    /// server no longer knows the request (404), so it is asked again with the same key. Anything transient - a
    /// 5xx from the proxy, a 429, no answer - is waited out; a long wait for an administrator must not be undone by
    /// one bad minute.</summary>
    private async Task<PollOutcome> PollRequestAsync(RecoveryState state, ECDsa key, DeviceIdentity.Snapshot old, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
            RekeyRequestStatus? status; int code;
            try
            {
                using var http = PlainClient();
                using var r = await http.GetAsync($"{BaseUrl}/enroll/rekey/{state.RequestId}?token={Uri.EscapeDataString(state.Token)}", ct);
                code = (int)r.StatusCode;
                status = code is 200 or 202 or 410 ? await r.Content.ReadFromJsonAsync(AgentJsonContext.Default.RekeyRequestStatus, ct) : null;
            }
            catch (HttpRequestException) { continue; } // network: keep polling
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { continue; }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, L.RekeyService_Failed); continue; }
            if (code == 404)
            {
                state.RequestId = Guid.Empty; state.Token = "";
                WriteJson(RecoveryPath, JsonSerializer.Serialize(state, AgentLocalJsonContext.Default.RecoveryState));
                return PollOutcome.Reopen;
            }
            if (code != 200 && code != 410) continue; // pending, throttled, a hiccup: keep waiting
            if (code == 200 && status?.Certificate is { } pem)
            {
                var thumb = DeviceKeyStore.InstallCertificate(pem, key);
                var snapshot = new DeviceIdentity.Snapshot(state.Provider, thumb, state.KeyName, null, status.NotAfter);
                DeviceIdentity.Set(snapshot);
                RewriteEnrollment(snapshot);
                try { File.Delete(RecoveryPath); } catch { /* best effort */ }
                DeviceKeyStore.RemoveCertificate(old.Thumbprint);
                logger.LogWarning(L.RekeyService_Recovered, state.Provider, status.NotAfter);
                reconnect.Request(); // the channel has been failing without a certificate; try at once
                return PollOutcome.Recovered;
            }
            if (code == 200) continue; // 200 without a certificate: not a decision yet
            // Rejected or expired: start over tomorrow.
            logger.LogError(L.RekeyService_RequestRejected, status?.State ?? code.ToString());
            try { File.Delete(RecoveryPath); } catch { /* best effort */ }
            DeviceKeyStore.DeleteKey(state.KeyName);
            return PollOutcome.Ended;
        }
    }

    // ---- plumbing -------------------------------------------------------------------------------------

    private void RewriteEnrollment(DeviceIdentity.Snapshot s)
    {
        var path = Path.Combine(_opt.EnrollmentDir, "enrollment.json");
        var rec = JsonSerializer.Deserialize(File.ReadAllText(path), AgentLocalJsonContext.Default.EnrollmentRecord)!;
        rec.CertThumbprint = s.Thumbprint;
        rec.KeyProvider = s.Provider;
        rec.KeyName = s.KeyName;
        rec.CertNotAfterUtc = s.NotAfter;
        WriteJson(path, JsonSerializer.Serialize(rec, AgentLocalJsonContext.Default.EnrollmentRecord));
    }

    /// <summary>Atomic write: a crash mid-write leaves the previous file, never a torn one.</summary>
    private static void WriteJson(string path, string json)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
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

    /// <summary>A client with no certificate (the device has none to offer), still pinned when a pin is set.</summary>
    private HttpClient PlainClient()
    {
        var handler = new SocketsHttpHandler();
        if (!string.IsNullOrWhiteSpace(_opt.Telemetry.ServerCertPinSha256))
            handler.SslOptions.RemoteCertificateValidationCallback = CertHelper.PinnedServerValidator(_opt.Telemetry.ServerCertPinSha256);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }
}
