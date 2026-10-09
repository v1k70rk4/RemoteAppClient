using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteAgent.Commands;
using RemoteServer.Data;
using RemoteServer.Data.Entities;
using RemoteServer.Hub;
using RemoteServer.Signing;
using L = RemoteServer.Localization.Strings;

namespace RemoteServer.Services;

/// <summary>
/// Manages the command queue (Commands table). The row stores intent; the signature is
/// created at delivery time with a fresh timestamp because the replay window prevents
/// pre-signing. Online devices receive push immediately; offline devices remain Queued.
/// </summary>
public sealed class CommandService(
    AppDbContext db,
    CommandSigner signer,
    AgentConnectionRegistry registry,
    ILogger<CommandService> logger)
{
    /// <summary>Adds a command to the queue and attempts immediate delivery when the device is online.</summary>
    public async Task<Command?> EnqueueAsync(string deviceId, string type, CommandData? data, Guid? createdBy, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
        {
            logger.LogWarning(L.CommandService_UnknownDeviceCommandDiscardedDevice, deviceId);
            return null;
        }

        var entity = new Command
        {
            DeviceId = device.Id,
            Type = type,
            PayloadJson = data is null ? null : JsonSerializer.Serialize(data, AgentJsonContext.Default.CommandData),
            Status = CommandStatus.Queued,
            CreatedByUserId = createdBy,
        };
        db.Commands.Add(entity);
        await db.SaveChangesAsync(ct);

        await TryDeliverAsync(device, entity, ct);
        return entity;
    }

    /// <summary>Delivers pending Queued commands when the device connects.</summary>
    public async Task DrainQueuedAsync(string deviceId, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null) return;

        var pending = await db.Commands
            .Where(c => c.DeviceId == device.Id && c.Status == CommandStatus.Queued)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        foreach (var cmd in pending)
            await TryDeliverAsync(device, cmd, ct);
    }

    private async Task TryDeliverAsync(Device device, Command entity, CancellationToken ct)
    {
        var deviceId = device.DeviceId;
        // Fresh signature at delivery time keeps the timestamp inside the replay window. The form follows the
        // agent's last reported version: the full, device-bound one for agents that check it, the original for
        // the rest (an agent that just updated and has not reported yet still accepts the original).
        var data = entity.PayloadJson is null
            ? null
            : JsonSerializer.Deserialize(entity.PayloadJson, AgentJsonContext.Default.CommandData);
        var signed = signer.Create(entity.Type, data, CommandSigner.UsesV2(device.AgentVersion) ? deviceId : null);

        // The nonce goes on the row BEFORE the send: a device on a fast link answers within milliseconds, and
        // when nothing else remembers who asked (a queued command delivered at a sleeping device's next wake),
        // the answer is attributed through this row. A failed send leaves a nonce that the next delivery
        // overwrites with its own fresh signature.
        entity.Nonce = signed.Nonce;
        entity.Signature = signed.Signature;
        await db.SaveChangesAsync(ct);

        var sent = await registry.TrySendAsync(deviceId, signed, ct);
        if (sent)
        {
            entity.Status = CommandStatus.Sent;
            entity.SentAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(L.CommandService_CommandDeliveredToDeviceType, deviceId, entity.Type);
        }
        else
        {
            logger.LogInformation(L.CommandService_DeviceOfflineCommandRemainsQueued, deviceId, entity.Type);
        }
    }
}
