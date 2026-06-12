using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteAgent.Commands;
using RemoteAgent.Telemetry;
using RemoteServer.Data;
using RemoteServer.Data.Entities;
using RemoteServer.Telemetry;

namespace RemoteServer.Services;

/// <summary>
/// A telemetriát MariaDB-be írja: frissíti a <see cref="Device"/> denormalizált
/// mezőit (gyors listázás) és beszúr egy append-only <see cref="DeviceTelemetry"/> sort.
/// </summary>
public sealed class DbTelemetrySink(AppDbContext db) : ITelemetrySink
{
    public async Task IngestAsync(string deviceId, TelemetryPayload payload, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
        {
            // Éles üzemben a gépet az enrollment hozza létre; addig a telemetria
            // bootstrapként Pending eszközként rögzíti, hogy a flow működjön.
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
