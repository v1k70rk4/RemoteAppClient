using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Commands;
using RemoteAgent.Configuration;
using RemoteAgent.Enrollment;
using RemoteAgent.Security;
using RemoteAgent.Vnc;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Services;

/// <summary>
/// Ensures VNC is ready at startup. If not provisioned yet, installs and hardens it with
/// a unique per-device password, then reports the password to the server over mTLS so
/// admins can connect. The password is stored locally in vnc.secret to avoid rotation
/// and tvnserver restart on every boot.
/// </summary>
public sealed class VncProvisioningService(
    IOptions<AgentOptions> options,
    ILogger<VncProvisioningService> logger) : BackgroundService
{
    private readonly AgentOptions _opt = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Local VNC lock: if an admin disabled this device, do not provision. Enforce
        // stopped+disabled instead. The lock can only be released locally.
        if (VncLock.IsLocked())
        {
            VncLock.Enforce();
            logger.LogWarning(L.VncProvisioningService_VNCIsLocallyDisabledProvisioning);
        }
        else
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
                        logger.LogInformation(L.VncProvisioningService_VNCProvisionedPerDevicePassword);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, L.VncProvisioningService_VNCProvisioningSkippedAdminSYSTEM);
                        password = null;
                    }
                }

                if (!string.IsNullOrEmpty(password))
                    await ReportSecretAsync(password, stoppingToken);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                logger.LogWarning(ex, L.VncProvisioningService_VNCPasswordReportFailed);
            }
        }

        // Continuous lock enforcement if it is disabled later or something restarts tvnserver.
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
            if (VncLock.IsLocked()) VncLock.Enforce();
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
            logger.LogInformation(L.VncProvisioningService_VNCPasswordReportedToThe);
        else
            logger.LogWarning(L.VncProvisioningService_VNCPasswordReportRejectedHTTP, (int)resp.StatusCode);
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
