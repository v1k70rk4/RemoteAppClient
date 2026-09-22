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
    RemoteAgent.Time.TimeSyncTrigger timeSync,
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
                var sentAt = DateTimeOffset.UtcNow;
                using var resp = await http.PostAsJsonAsync(
                    _opt.IngestUrl, payload, AgentJsonContext.Default.TelemetryPayload, stoppingToken);

                if (resp.IsSuccessStatusCode)
                {
                    status.MarkServerContact(); // status-pipe "last server contact"
                    CheckClockAgainst(resp.Headers.Date, DateTimeOffset.UtcNow - sentAt);
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

    /// <summary>
    /// Compares our clock to the server's, using the Date header every telemetry response already carries.
    /// <para>
    /// This is the only channel that keeps working once the clock is wrong: telemetry is not signed, while
    /// every COMMAND is refused outside a 60s window - so a drifted machine can no longer be told anything,
    /// including how to fix itself. Checking here means we notice within one telemetry cycle instead of
    /// waiting for the periodic sweep, or for a command that will never be accepted anyway.
    /// </para>
    /// <para>
    /// The header has one-second resolution and the round trip adds a little more; that is irrelevant next
    /// to the tens of seconds that actually break command delivery, so the threshold is deliberately coarse.
    /// The clock is never set from this value - it only asks the sync service to go consult a real time source.
    /// </para>
    /// <para>
    /// A laptop that dozed off between sending and reading the reply would compare the server's stamp from
    /// before the nap with its own clock after it, and call the nap's length a clock error. A real round trip
    /// is well under a second, so a reply that took long enough to matter is simply not read.
    /// </para>
    /// </summary>
    private void CheckClockAgainst(DateTimeOffset? serverTime, TimeSpan roundTrip)
    {
        if (serverTime is not { } t) return;                      // no Date header; nothing to compare
        if (roundTrip < TimeSpan.Zero || roundTrip > MaxTrustedRoundTrip) return;
        var skew = Math.Abs((DateTimeOffset.UtcNow - t).TotalSeconds);
        if (skew < ClockSkewTriggerSeconds) return;

        try { logger.LogWarning("Ora-elteres a szerverhez kepest: {Skew:F0}s - idoszinkron kerese.", skew); } catch { }
        timeSync.Request();                                       // rate-limited inside TimeSyncService
    }

    /// <summary>Half the agent's 60s command window: react while commands still get through, not after.</summary>
    private const int ClockSkewTriggerSeconds = 30;

    /// <summary>Longer than this and something other than the network happened in between.</summary>
    private static readonly TimeSpan MaxTrustedRoundTrip = TimeSpan.FromSeconds(10);

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
