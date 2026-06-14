namespace RemoteServer.Data.Entities;

/// <summary>Trace of admin actions: token generation, tunnel open, approval, revoke.</summary>
public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Actor: user GUID string or "system".</summary>
    public string Actor { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;
    public Guid? TargetDeviceId { get; set; }

    /// <summary>Additional details as JSON.</summary>
    public string? DetailJson { get; set; }

    public string? Ip { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
