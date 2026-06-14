using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Configuration;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Services;

/// <summary>
/// Periodically updates the heartbeat file (&lt;EnrollmentDir&gt;\agent.heartbeat). The Helper
/// (RemoteAgent.Updater) watches it: if the heartbeat is stale while the service is "running",
/// the agent is hung. SCM cannot see that, only process exit. The Helper recovers through
/// stop, optional kill, and restart. Deliberately cheap signal: one file timestamp, no IPC.
/// </summary>
public sealed class HeartbeatService(IOptions<AgentOptions> options, ILogger<HeartbeatService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private readonly string _file = Path.Combine(options.Value.EnrollmentDir, "agent.heartbeat");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(_file)!); } catch { /* best effort */ }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await File.WriteAllTextAsync(_file, DateTimeOffset.UtcNow.ToString("O"), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogDebug(ex, L.HeartbeatService_001); }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
