using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Commands;
using RemoteAgent.Configuration;
using RemoteAgent.Security;
using RemoteAgent.Telemetry;
using RemoteAgent.Tunnel;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Services;

/// <summary>
/// Periodically collects and sends device telemetry to the server-side API over mTLS with
/// server pinning. It intentionally never writes directly to SQL; the DB stays behind the API.
/// </summary>
public sealed class TelemetryService(
    IOptions<AgentOptions> options,
    SystemInfoCollector collector,
    AgentStatusState status,
    TransportState transport,
    ILogger<TelemetryService> logger) : BackgroundService
{
    private readonly TelemetryOptions _opt = options.Value.Telemetry;
    private readonly string _pfxPath = options.Value.ClientCertPfxPath;
    private readonly SemaphoreSlim _wake = new(0, 1);   // pulsed by PowerMonitor to send immediately on plug/unplug

    private void WakeNow() { try { if (_wake.CurrentCount == 0) _wake.Release(); } catch { /* a send is already pending */ } }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_opt.IngestUrl))
        {
            logger.LogWarning(L.TelemetryService_NoTelemetryURLConfiguredService);
            return;
        }

        var interval = TimeSpan.FromSeconds(_opt.IntervalSeconds);
        HttpClient? http = null; // built lazily; rebuilt after cert errors, for example after enrollment

        // Event-driven power: send telemetry immediately when the charger is plugged/unplugged instead of
        // waiting out the interval. PowerMonitor also provides a reliable AC state for this Session-0 service.
        PowerMonitor.Changed += WakeNow;
        PowerMonitor.Start();
        try { await Task.Delay(500, stoppingToken); } catch (OperationCanceledException) { } // let the initial AC state arrive

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                http ??= BuildClient(); // may throw before the cert exists; caught so host keeps running
                var payload = collector.Collect();
                using var resp = await http.PostAsJsonAsync(
                    _opt.IngestUrl, payload, AgentJsonContext.Default.TelemetryPayload, stoppingToken);

                if (resp.IsSuccessStatusCode)
                {
                    status.MarkServerContact(); // status-pipe "last server contact"
                    try
                    {
                        // The server steers the bastion transport via the response body. Older servers
                        // return no body, so a parse failure here is harmless and ignored.
                        var cfg = await resp.Content.ReadFromJsonAsync(
                            AgentJsonContext.Default.AgentConfigResponse, stoppingToken);
                        if (cfg is not null) transport.SetTransport(cfg.BastionTransport);
                    }
                    catch { /* no/invalid config body; keep the current transport */ }
                    logger.LogDebug(L.TelemetryService_TelemetrySent);
                }
                else
                    logger.LogWarning(L.TelemetryService_TelemetryRejectedHTTPCode, (int)resp.StatusCode);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, L.TelemetryService_TelemetriaSendingFailed);
                http?.Dispose();
                http = null; // rebuild on next cycle, for example if enrollment completed meanwhile
            }

            try { await _wake.WaitAsync(interval, stoppingToken); }   // wakes early on a power-source change
            catch (OperationCanceledException) { break; }
        }

        PowerMonitor.Changed -= WakeNow;
        PowerMonitor.Stop();
        http?.Dispose();
    }

    private HttpClient BuildClient()
    {
        var handler = new SocketsHttpHandler();

        if (!string.IsNullOrWhiteSpace(_pfxPath) || !string.IsNullOrWhiteSpace(_opt.ClientCertThumbprint))
        {
            handler.SslOptions.ClientCertificates ??= new();
            handler.SslOptions.ClientCertificates.Add(
                CertHelper.ResolveClientCertificate(_pfxPath, _opt.ClientCertThumbprint));
        }

        if (!string.IsNullOrWhiteSpace(_opt.ServerCertPinSha256))
            handler.SslOptions.RemoteCertificateValidationCallback =
                CertHelper.PinnedServerValidator(_opt.ServerCertPinSha256);

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }
}
