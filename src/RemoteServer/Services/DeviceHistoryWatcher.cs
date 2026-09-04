using Microsoft.EntityFrameworkCore;
using RemoteServer.Data;
using RemoteServer.Data.Entities;
using RemoteServer.Hub;

namespace RemoteServer.Services;

/// <summary>
/// Records when a device changes liveness state (online / flaky / not-controllable / offline) and prunes
/// history past the retention window.
///
/// State is not a stored column - it is derived from the socket registry, telemetry freshness and reconnect
/// churn - so there is no write to hang an event off. Sampling it here instead keeps the recorded history
/// identical to what the console shows, and a stable fleet writes nothing at all: only transitions land.
/// IP changes are written where they happen, in <see cref="DbTelemetrySink"/>.
/// </summary>
public sealed class DeviceHistoryWatcher(
    IServiceScopeFactory scopeFactory,
    AgentConnectionRegistry registry,
    ILogger<DeviceHistoryWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(6);

    /// <summary>Last state seen per device, so only transitions are written.</summary>
    private readonly Dictionary<Guid, string> _lastState = [];
    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the fleet reconnect after a restart before sampling, or every device would look offline once
        // and immediately flip back - noise in the history that says nothing about the devices.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SampleAsync(stoppingToken); }
            catch (Exception ex) { logger.LogWarning(ex, "Device history sweep failed."); }

            try { await Task.Delay(Interval, stoppingToken); } catch { return; }
        }
    }

    private async Task SampleAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        var devices = await db.Devices
            .Where(d => !d.DeviceId.StartsWith("opsrc:"))   // synthetic source-IP lock records, not real devices
            .Select(d => new { d.Id, d.DeviceId, d.LastSeenAt })
            .ToListAsync(ct);

        var events = new List<DeviceEvent>();
        foreach (var d in devices)
        {
            var state = DeviceLiveness.State(
                registry.IsConnected(d.DeviceId),
                d.LastSeenAt > now - DeviceLiveness.FreshWindow,
                registry.RecentReconnects(d.DeviceId));

            if (_lastState.TryGetValue(d.Id, out var previous))
            {
                if (previous == state) continue;
                events.Add(new DeviceEvent { DeviceId = d.Id, At = now, Kind = DeviceEventKinds.State, OldValue = previous, NewValue = state });
            }
            else
            {
                // First sighting after a server start: record where the device stands, with no "from", so the
                // history does not pretend a transition happened just because we restarted.
                events.Add(new DeviceEvent { DeviceId = d.Id, At = now, Kind = DeviceEventKinds.State, OldValue = null, NewValue = state });
            }

            _lastState[d.Id] = state;
        }

        // Devices that have been deleted should not keep a slot in the dictionary.
        if (_lastState.Count > devices.Count)
        {
            var live = devices.Select(d => d.Id).ToHashSet();
            foreach (var gone in _lastState.Keys.Where(k => !live.Contains(k)).ToList()) _lastState.Remove(gone);
        }

        if (events.Count > 0)
        {
            db.DeviceEvents.AddRange(events);
            await db.SaveChangesAsync(ct);
        }

        if (now >= _nextPrune)
        {
            _nextPrune = now + PruneEvery;
            var cutoff = now - Retention;
            var pruned = await db.DeviceEvents.Where(e => e.At < cutoff).ExecuteDeleteAsync(ct);
            if (pruned > 0) logger.LogInformation("Pruned {Count} device history entries older than {Days} days.", pruned, Retention.TotalDays);
        }
    }
}
