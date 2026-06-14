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
public sealed class DbTelemetrySink(AppDbContext db) : ITelemetrySink
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
    }
}
