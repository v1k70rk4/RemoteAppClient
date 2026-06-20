using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Configuration;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Services;

/// <summary>
/// Periodically bumps the agent liveness tick (<see cref="AgentStatusState.Heartbeat"/>), which the
/// Helper (RemoteAgent.Updater) reads over the status pipe as StatusReport.LastHeartbeatUtc: if the
/// tick is stale while the service is "running", the agent is hung. SCM cannot see that, only process
/// exit. The Helper recovers through stop, optional kill, and restart. The legacy heartbeat file
/// (&lt;EnrollmentDir&gt;\agent.heartbeat) is written only while the installed Helper is older than 1.8.1
/// (file-based); once the co-located Helper is pipe-aware the agent stops writing it automatically.
/// </summary>
public sealed class HeartbeatService(IOptions<AgentOptions> options, AgentStatusState status, RemoteAgent.Telemetry.SystemInfoCollector sysInfo, ILogger<HeartbeatService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private static readonly Version PipeAwareHelper = new(1, 8, 1, 0); // first Helper that reads liveness over the status pipe
    private readonly string _file = Path.Combine(options.Value.EnrollmentDir, "agent.heartbeat");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(_file)!); } catch { /* best effort */ }

        while (!stoppingToken.IsCancellationRequested)
        {
            status.Heartbeat(); // primary liveness signal: the Helper reads it over the status pipe (StatusReport.LastHeartbeatUtc)

            // Legacy heartbeat file: only needed while an older, file-based Helper is installed. As soon as the
            // co-located Helper is pipe-aware (>= 1.8.1) it reads the tick over the pipe, so this self-retires.
            if (!HelperReadsPipe())
            {
                try
                {
                    await File.WriteAllTextAsync(_file, DateTimeOffset.UtcNow.ToString("O"), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogDebug(ex, L.HeartbeatService_HeartbeatWriteFailed); }
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>True when the installed Helper reads liveness over the status pipe (>= 1.8.1), making the legacy
    /// heartbeat file redundant. Unknown or older version → false: keep writing the file (fail safe).</summary>
    private bool HelperReadsPipe() =>
        Version.TryParse(sysInfo.ComponentVersions().Helper, out var v) && v >= PipeAwareHelper;
}
