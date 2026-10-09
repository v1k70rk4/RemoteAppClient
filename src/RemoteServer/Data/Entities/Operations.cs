namespace RemoteServer.Data.Entities;

/// <summary>Enrollment token, one-time by default. Token equals permission for one install.</summary>
public sealed class EnrollmentToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Token hash. The raw token is visible only when issued.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public Guid? CreatedByUserId { get; set; }

    /// <summary>Group assigned to the enrolled device.</summary>
    public Guid? GroupId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }

    public int MaxUses { get; set; } = 1;
    public int UseCount { get; set; }

    /// <summary>
    /// True: enrolled device becomes Approved immediately (admin-issued one-time token).
    /// False: device becomes Pending and waits for approval (site/bootstrap self-service install).
    /// </summary>
    public bool AutoApprove { get; set; } = true;

    public DateTimeOffset? UsedAt { get; set; }
    public Guid? UsedByDeviceId { get; set; }

    /// <summary>Revoked by admin; token can no longer be used for enrollment.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public string? Note { get; set; }

    /// <summary>
    /// Generated MSI file name when this token belongs to an MSI, stored under PackagesDir
    /// and served by /admin/msi/{fileName}. Null for manual tokens/blobs.
    /// </summary>
    public string? MsiFileName { get; set; }
}

/// <summary>
/// Unified command queue: tunnel, restart, exec, update all live here by type.
/// For offline devices, commands wait as Queued and run when the device comes online.
/// </summary>
public sealed class Command
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }

    /// <summary>open-tunnel / close-tunnel / exec / restart / update / …</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Command-specific parameters as JSON.</summary>
    public string? PayloadJson { get; set; }

    public CommandStatus Status { get; set; } = CommandStatus.Queued;

    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Result returned by the device as JSON.</summary>
    public string? ResultJson { get; set; }

    // Nonce and signature of the issued signed command for audit and replay tracking.
    public string? Nonce { get; set; }
    public string? Signature { get; set; }
}

/// <summary>
/// A package published to a release channel. Channel (rtm/beta) + component (agent/updater)
/// + version. The current package for a (channel, component) is the latest UploadedAt.
/// File lives in PackagesDir and is served by /api/updates/{FileName}.
/// </summary>
public sealed class ReleasePackage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Release channel: "rtm" (stable) or "beta" (test ring).</summary>
    public string Channel { get; set; } = "rtm";

    /// <summary>Component: "agent" or "updater".</summary>
    public string Component { get; set; } = "agent";

    public string Version { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A remote viewing session: who viewed which device and when, for privacy audit.</summary>
public sealed class RemoteSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public int RemotePort { get; set; }

    public Guid? OpenedByUserId { get; set; }
    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }

    public ConsentState ConsentState { get; set; } = ConsentState.NotRequired;
}

/// <summary>
/// A device's request for a new certificate when it has no key left to authenticate with (its TPM was cleared).
/// Opened without a certificate, decided by an administrator in the console; the device polls with the one-time
/// token. At most one pending per device; expires after seven days.
/// </summary>
public sealed class RekeyRecoveryRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The device's row key (<see cref="Device.Id"/>).</summary>
    public Guid DeviceKey { get; set; }

    public string Hostname { get; set; } = string.Empty;
    public string? SourceIp { get; set; }

    /// <summary>PKCS#10 (PEM) for the device's new key; signed only on approval.</summary>
    public string Csr { get; set; } = string.Empty;
    public string KeyProvider { get; set; } = string.Empty;

    /// <summary>SHA-256 of the polling token; the raw token goes to the device once.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>"pending" | "approved" | "rejected".</summary>
    public string State { get; set; } = "pending";
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }

    /// <summary>The issued certificate (PEM), for the device to fetch, once approved.</summary>
    public string? CertificatePem { get; set; }
    public DateTimeOffset? CertNotAfter { get; set; }
}
