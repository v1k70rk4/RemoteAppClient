using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Commands;
using RemoteAgent.Configuration;
using RemoteAgent.Enrollment;
using RemoteAgent.Security;
using RemoteAgent.Vnc;

namespace RemoteAgent.Services;

/// <summary>
/// Induláskor gondoskodik a VNC-ről: ha még nincs provisionálva, telepíti+hardeneli
/// egy gépenként egyedi jelszóval, majd a jelszót jelenti a szervernek (mTLS), hogy
/// az admin tudjon csatlakozni. A jelszót helyben is eltárolja (vnc.secret), hogy
/// újraindításkor ne rotálódjon (és ne kelljen újraindítani a tvnservert).
/// </summary>
public sealed class VncProvisioningService(
    IOptions<AgentOptions> options,
    ILogger<VncProvisioningService> logger) : BackgroundService
{
    private readonly AgentOptions _opt = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var secretFile = Path.Combine(_opt.EnrollmentDir, "vnc.secret");
            string? password = File.Exists(secretFile) ? File.ReadAllText(secretFile).Trim() : null;

            if (string.IsNullOrEmpty(password))
            {
                password = VncProvisioner.GeneratePassword();
                var msi = Path.Combine(AppContext.BaseDirectory, "vnc", "tightvnc.msi");
                try
                {
                    await VncProvisioner.EnsureInstalledAsync(msi);
                    VncProvisioner.ApplyHardening(password);
                    File.WriteAllText(secretFile, password);
                    logger.LogInformation("VNC provisionálva, gépenkénti jelszó beállítva.");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "VNC provisioning kihagyva (admin/SYSTEM jog kell).");
                    return;
                }
            }

            await ReportSecretAsync(password, stoppingToken);
        }
        catch (OperationCanceledException) { /* leállás */ }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "VNC-jelszó jelentése sikertelen.");
        }
    }

    private async Task ReportSecretAsync(string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.Telemetry.IngestUrl))
            return;

        var baseUri = new Uri(_opt.Telemetry.IngestUrl);
        var url = $"{baseUri.Scheme}://{baseUri.Authority}/api/vnc-secret";

        using var http = BuildClient();
        using var resp = await http.PostAsJsonAsync(
            url, new VncSecretReport { Secret = password }, AgentJsonContext.Default.VncSecretReport, ct);

        if (resp.IsSuccessStatusCode)
            logger.LogInformation("VNC-jelszó jelentve a szervernek.");
        else
            logger.LogWarning("VNC-jelszó jelentése elutasítva: HTTP {Code}", (int)resp.StatusCode);
    }

    private HttpClient BuildClient()
    {
        var handler = new SocketsHttpHandler();

        if (!string.IsNullOrWhiteSpace(_opt.ClientCertPfxPath) || !string.IsNullOrWhiteSpace(_opt.Telemetry.ClientCertThumbprint))
        {
            handler.SslOptions.ClientCertificates ??= new();
            handler.SslOptions.ClientCertificates.Add(
                CertHelper.ResolveClientCertificate(_opt.ClientCertPfxPath, _opt.Telemetry.ClientCertThumbprint));
        }

        if (!string.IsNullOrWhiteSpace(_opt.Telemetry.ServerCertPinSha256))
            handler.SslOptions.RemoteCertificateValidationCallback =
                CertHelper.PinnedServerValidator(_opt.Telemetry.ServerCertPinSha256);

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }
}
