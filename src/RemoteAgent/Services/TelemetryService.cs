using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Commands;
using RemoteAgent.Configuration;
using RemoteAgent.Security;
using RemoteAgent.Telemetry;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Services;

/// <summary>
/// Periodikusan összegyűjti és elküldi a gép-telemetriát a szerver-oldali API-ba
/// (mTLS + szerver-pin). Szándékosan NEM ír közvetlenül SQL-be: a DB az API mögött marad.
/// </summary>
public sealed class TelemetryService(
    IOptions<AgentOptions> options,
    SystemInfoCollector collector,
    AgentStatusState status,
    ILogger<TelemetryService> logger) : BackgroundService
{
    private readonly TelemetryOptions _opt = options.Value.Telemetry;
    private readonly string _pfxPath = options.Value.ClientCertPfxPath;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_opt.IngestUrl))
        {
            logger.LogWarning(L.TelemetryService_001);
            return;
        }

        var interval = TimeSpan.FromSeconds(_opt.IntervalSeconds);
        HttpClient? http = null; // lustán épül; cert-hiba esetén újraépül (pl. enrollment után)

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                http ??= BuildClient(); // itt dobhat, ha a cert még nincs — elkapjuk, nem áll le a host
                var payload = collector.Collect();
                using var resp = await http.PostAsJsonAsync(
                    _opt.IngestUrl, payload, AgentJsonContext.Default.TelemetryPayload, stoppingToken);

                if (resp.IsSuccessStatusCode)
                {
                    status.MarkServerContact(); // a status-pipe „utolsó szerver-kontakt"-ja
                    logger.LogDebug(L.TelemetryService_002);
                }
                else
                    logger.LogWarning(L.TelemetryService_003, (int)resp.StatusCode);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, L.TelemetryService_004);
                http?.Dispose();
                http = null; // a következő ciklusban újraépül (pl. ha közben megtörtént a beléptetés)
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

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
