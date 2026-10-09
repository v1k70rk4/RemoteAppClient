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
public sealed class RekeyService(IOptions<AgentOptions> options, ILogger<RekeyService> logger) : BackgroundService
{
    private readonly AgentOptions _opt = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (DeviceIdentity.Current is null || string.IsNullOrWhiteSpace(_opt.Telemetry.IngestUrl)) return;
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

            // Prove the new certificate at the server before anything old is touched.
            using (var http = BuildClient(candidate))
            {
                using var r = await http.PostAsync($"{baseUrl}/api/rekey/confirm", content: null, ct);
                if (!r.IsSuccessStatusCode)
                {
                    logger.LogWarning(L.RekeyService_ConfirmFailed, (int)r.StatusCode);
                    DeviceKeyStore.RemoveCertificate(newThumb);
                    return false;
                }
            }

            // Switch: every later connection uses the new identity; enrollment.json says so for the next start.
            DeviceIdentity.Set(candidate);
            RewriteEnrollment(candidate);
            if (id.InStore) { DeviceKeyStore.RemoveCertificate(id.Thumbprint); DeviceKeyStore.DeleteKey(id.KeyName); }
            else if (id.PfxPath is { } pfx) { try { System.IO.File.Delete(pfx); } catch { /* best effort */ } }
            logger.LogWarning(L.RekeyService_Rekeyed, reason, fresh.Provider, resp.NotAfter);
            return true;
        }
        catch
        {
            if (newThumb is not null) DeviceKeyStore.RemoveCertificate(newThumb);
            DeviceKeyStore.DeleteKey(fresh.KeyName);
            throw;
        }
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

    private HttpClient BuildClient(DeviceIdentity.Snapshot id)
    {
        var handler = new SocketsHttpHandler();
        handler.SslOptions.ClientCertificates ??= new();
        handler.SslOptions.ClientCertificates.Add(CertHelper.ResolveClientCertificate(id.PfxPath, id.Thumbprint));
        if (!string.IsNullOrWhiteSpace(_opt.Telemetry.ServerCertPinSha256))
            handler.SslOptions.RemoteCertificateValidationCallback = CertHelper.PinnedServerValidator(_opt.Telemetry.ServerCertPinSha256);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }
}
