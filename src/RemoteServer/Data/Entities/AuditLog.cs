namespace RemoteServer.Data.Entities;

/// <summary>Minden admin-művelet nyoma: token-gyártás, tunnel-nyit, jóváhagyás, visszavonás.</summary>
public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Ki tette (user GUID stringként, vagy "system").</summary>
    public string Actor { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;
    public Guid? TargetDeviceId { get; set; }

    /// <summary>További részletek JSON-ként.</summary>
    public string? DetailJson { get; set; }

    public string? Ip { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
