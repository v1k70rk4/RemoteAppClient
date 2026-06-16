using Microsoft.EntityFrameworkCore;
using RemoteServer.Data;
using RemoteServer.Data.Entities;

namespace RemoteServer.Services;

/// <summary>
/// Expires stale device commands so they cannot pile up and flood an agent's single serial command
/// consumer on reconnect.
///
/// Two cutoffs, because the states mean different things:
///  - Sent/Acked = delivered but never finished (the agent is online). These are the ones that
///    accumulate and jam; expire them quickly (<see cref="DeliveredMaxAge"/>).
///  - Queued = legitimately waiting for an OFFLINE device to reconnect, so keep them for a long time
///    (<see cref="QueuedMaxAge"/>) — expiring these would wrongly clear the pending-update indicator
///    for offline machines. (When such a device returns, auto-converge re-issues by version anyway.)
/// </summary>
public sealed class CommandExpiryWatcher(IServiceScopeFactory scopeFactory, ILogger<CommandExpiryWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan DeliveredMaxAge = TimeSpan.FromMinutes(20); // Sent/Acked but stuck
    private static readonly TimeSpan QueuedMaxAge = TimeSpan.FromDays(7);        // truly-stale offline backlog
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTimeOffset.UtcNow;
                var deliveredCutoff = now - DeliveredMaxAge;
                var queuedCutoff = now - QueuedMaxAge;
                var n = await db.Commands
                    .Where(c =>
                        ((c.Status == CommandStatus.Sent || c.Status == CommandStatus.Acked) && c.CreatedAt < deliveredCutoff)
                        || (c.Status == CommandStatus.Queued && c.CreatedAt < queuedCutoff))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.Status, CommandStatus.Failed)
                        .SetProperty(c => c.CompletedAt, now), stoppingToken);
                if (n > 0) logger.LogInformation("Expired {Count} stale commands.", n);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Command expiry sweep failed."); }

            try { await Task.Delay(Interval, stoppingToken); } catch { return; }
        }
    }
}
