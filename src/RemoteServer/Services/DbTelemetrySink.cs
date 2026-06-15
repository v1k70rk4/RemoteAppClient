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
    /// Nudges the device toward its channel's current agent/updater package (the channel "target"/min
    /// version) so devices installed or approved after a rollout still update. Upgrade-only; respects
    /// UpdateAllowed + approval, and backs off while an update is in flight or recently failed.
    /// </summary>
    private async Task AutoConvergeAsync(Device device, CancellationToken ct)
    {
        if (!device.UpdateAllowed || device.Status != DeviceStatus.Approved) return;

        var cutoff = DateTimeOffset.UtcNow.AddHours(-6);
        bool busy = await db.Commands.AnyAsync(c => c.DeviceId == device.Id && c.Type == CommandTypes.Update
            && ((c.Status == CommandStatus.Queued || c.Status == CommandStatus.Sent || c.Status == CommandStatus.Acked)
                || (c.Status == CommandStatus.Failed && c.CreatedAt > cutoff)), ct);
        if (busy) return; // update in flight, or a recent failure — retry later

        var channel = string.IsNullOrWhiteSpace(device.Channel) ? "rtm" : device.Channel;
        foreach (var comp in new[] { "agent", "updater" })
        {
            var pkg = await db.ReleasePackages.Where(p => p.Channel == channel && p.Component == comp)
                .OrderByDescending(p => p.UploadedAt).FirstOrDefaultAsync(ct);
            if (pkg is null || !Version.TryParse(pkg.Version, out var target)) continue;
            var reported = comp == "updater" ? device.HelperVersion : device.AgentVersion;
            if (Version.TryParse(reported, out var cur) && cur >= target) continue; // already at/above target

            var data = new CommandData
            {
                UpdateVersion = pkg.Version, UpdateUrl = $"/api/updates/{pkg.FileName}",
                UpdateSha256 = pkg.Sha256, UpdateTarget = comp,
            };
            await commands.EnqueueAsync(device.DeviceId, CommandTypes.Update, data, null, ct);
            return; // one component at a time; the other converges on a later telemetry
        }
    }
}
