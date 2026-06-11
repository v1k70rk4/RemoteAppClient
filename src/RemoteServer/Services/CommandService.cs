using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteAgent.Commands;
using RemoteServer.Data;
using RemoteServer.Data.Entities;
using RemoteServer.Hub;
using RemoteServer.Signing;

namespace RemoteServer.Services;

/// <summary>
/// A parancs-sor (Commands tábla) kezelése. A sor a SZÁNDÉKOT tárolja; az aláírás
/// mindig KÉZBESÍTÉSKOR készül friss időbélyeggel (a replay-ablak miatt nem lehet
/// előre aláírni). Online gépnek azonnal push, offline-nak Queued marad.
/// </summary>
public sealed class CommandService(
    AppDbContext db,
    CommandSigner signer,
    AgentConnectionRegistry registry,
    ILogger<CommandService> logger)
{
    /// <summary>Új parancs a sorba + azonnali kézbesítési kísérlet, ha a gép online.</summary>
    public async Task<Command?> EnqueueAsync(string deviceId, string type, CommandData? data, Guid? createdBy, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null)
        {
            logger.LogWarning("Ismeretlen gép, parancs eldobva: {Device}", deviceId);
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

        await TryDeliverAsync(deviceId, entity, ct);
        return entity;
    }

    /// <summary>A gép csatlakozásakor: a függő (Queued) parancsok kézbesítése.</summary>
    public async Task DrainQueuedAsync(string deviceId, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null) return;

        var pending = await db.Commands
            .Where(c => c.DeviceId == device.Id && c.Status == CommandStatus.Queued)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        foreach (var cmd in pending)
            await TryDeliverAsync(deviceId, cmd, ct);
    }

    private async Task TryDeliverAsync(string deviceId, Command entity, CancellationToken ct)
    {
        // FRISS aláírás kézbesítéskor — így az időbélyeg mindig az ablakon belül van.
        var data = entity.PayloadJson is null
            ? null
            : JsonSerializer.Deserialize(entity.PayloadJson, AgentJsonContext.Default.CommandData);
        var signed = signer.Create(entity.Type, data);

        var sent = await registry.TrySendAsync(deviceId, signed, ct);
        if (sent)
        {
            entity.Status = CommandStatus.Sent;
            entity.SentAt = DateTimeOffset.UtcNow;
            entity.Nonce = signed.Nonce;
            entity.Signature = signed.Signature;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Parancs kézbesítve {Device}: {Type}", deviceId, entity.Type);
        }
        else
        {
            logger.LogInformation("Gép offline, parancs sorban marad {Device}: {Type}", deviceId, entity.Type);
        }
    }
}
