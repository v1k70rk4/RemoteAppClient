using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteAgent.Commands;
using RemoteAgent.Telemetry;
using RemoteServer.Data;
using RemoteServer.Data.Entities;
using RemoteServer.Telemetry;

namespace RemoteServer.Services;

/// <summary>
/// Writes telemetry to MariaDB: updates denormalized <see cref="Device"/> fields for
/// fast listing and inserts an append-only <see cref="DeviceTelemetry"/> row.
/// </summary>
public sealed class DbTelemetrySink(AppDbContext db, CommandService commands) : ITelemetrySink
{
    public async Task IngestAsync(string deviceId, TelemetryPayload payload, string? publicIp, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
        {
            // In production enrollment creates devices. Until then, telemetry bootstraps
            // a Pending device so the flow keeps working.
            device = new Device { DeviceId = deviceId, Status = DeviceStatus.Pending, EnrolledAt = now };
            db.Devices.Add(device);
        }

        device.Hostname = payload.Hostname;
        device.OsVersion = payload.OsVersion;
        device.Manufacturer = payload.Manufacturer;
        device.Model = payload.Model;
        device.SerialNumber = payload.SerialNumber;
        device.AgentVersion = payload.AgentVersion;
        device.HelperVersion = payload.HelperVersion;
        device.VncVersion = payload.VncVersion;
        device.ClientVersion = payload.ClientVersion;
        device.AgentRestarts = payload.AgentRestarts;
        device.LastIncident = payload.LastIncident;
        device.VncLocked = payload.VncLocked;
        device.BootTimeUtc = payload.BootTimeUtc == default ? null : payload.BootTimeUtc;
        device.IpAddress = payload.IpAddress;
        if (!string.IsNullOrWhiteSpace(publicIp)) device.PublicIpAddress = publicIp;
        device.WifiSsid = payload.WifiSsid;
        device.VpnActive = payload.VpnActive;
        device.LoggedInUser = payload.LoggedInUser;
        device.LastSeenAt = now;

        db.DeviceTelemetry.Add(new DeviceTelemetry
        {
            DeviceId = device.Id,
            CollectedAt = now,
            PayloadJson = JsonSerializer.Serialize(payload, AgentJsonContext.Default.TelemetryPayload),
        });

        await db.SaveChangesAsync(ct);

        // Best-effort: keep the device converging to its channel's target package (never fail telemetry).
        try { await AutoConvergeAsync(device, ct); } catch { /* convergence is best-effort */ }
    }

    /// <summary>
    /// Converges the device toward its channel's current packages (the channel "target"/min versions) so
    /// devices installed or approved after a rollout still update. Sends ONE component per telemetry pass
    /// in a safe order (agent LAST — updating the agent restarts it), so a batch of pending packages rolls
    /// out one at a time instead of racing. Upgrade-only; respects UpdateAllowed + approval. Self-heals a
    /// racy multi-rollout because each pass simply sends whatever is still behind.
    /// </summary>
    private async Task AutoConvergeAsync(Device device, CancellationToken ct)
    {
        if (!device.UpdateAllowed || device.Status != DeviceStatus.Approved) return;
        var channel = string.IsNullOrWhiteSpace(device.Channel) ? "rtm" : device.Channel;

        // Circuit breaker: if a device has had a storm of update commands in the last hour, it cannot
        // apply something (e.g. a locked exe) — stop hammering it and flag it instead of looping forever.
        var hourAgo = DateTimeOffset.UtcNow.AddHours(-1);
        var recentUpdates = await db.Commands.CountAsync(
            c => c.DeviceId == device.Id && c.Type == CommandTypes.Update && c.CreatedAt > hourAgo, ct);
        if (recentUpdates >= 8)
        {
            const string note = "auto-converge paused: too many update attempts";
            if (device.LastIncident != note) { device.LastIncident = note; await db.SaveChangesAsync(ct); }
            return;
        }

        // "In progress" = a recently-sent update whose target version the device has NOT reported yet.
        // Update commands never report completion (Sent stays Sent), so the reported version is the
        // done-signal; the 10-minute bound lets a failed update stop blocking and be retried.
        var since = DateTimeOffset.UtcNow.AddMinutes(-10);
        var inflight = await db.Commands
            .Where(c => c.DeviceId == device.Id && c.Type == CommandTypes.Update && c.CreatedAt > since
                && (c.Status == CommandStatus.Queued || c.Status == CommandStatus.Sent || c.Status == CommandStatus.Acked))
            .ToListAsync(ct);
        foreach (var c in inflight)
        {
            var cd = c.PayloadJson is null ? null : JsonSerializer.Deserialize(c.PayloadJson, AgentJsonContext.Default.CommandData);
            if (Behind(Reported(device, cd?.UpdateTarget), cd?.UpdateVersion))
                return; // a sent update has not taken effect yet — let it finish before sending the next
        }

        foreach (var comp in new[] { "vnc", "client", "updater", "agent" })
        {
            var pkg = await db.ReleasePackages.Where(p => p.Channel == channel && p.Component == comp)
                .OrderByDescending(p => p.UploadedAt).FirstOrDefaultAsync(ct);
            if (pkg is null || !Behind(Reported(device, comp), pkg.Version)) continue;

            var data = new CommandData
            {
                UpdateVersion = pkg.Version, UpdateUrl = $"/api/updates/{pkg.FileName}",
                UpdateSha256 = pkg.Sha256, UpdateTarget = comp,
            };
            await commands.EnqueueAsync(device.DeviceId, CommandTypes.Update, data, null, ct);
            return; // one component per pass
        }
    }

    /// <summary>The device's reported version for an update component.</summary>
    private static string? Reported(Device d, string? component) => component switch
    {
        "updater" or "helper" => d.HelperVersion,
        "vnc" => d.VncVersion,
        "client" => d.ClientVersion,
        _ => d.AgentVersion,
    };

    /// <summary>True when the device should still update: target parses and reported is missing or below it (upgrade-only).</summary>
    private static bool Behind(string? reported, string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        if (Version.TryParse(target, out var t) && Version.TryParse(reported, out var r)) return r < t;
        return string.IsNullOrWhiteSpace(reported) || !reported.StartsWith(target, StringComparison.OrdinalIgnoreCase);
    }
}
