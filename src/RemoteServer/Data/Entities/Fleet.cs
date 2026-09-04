namespace RemoteServer.Data.Entities;

/// <summary>Device group. consent/unattended defaults are decided at group level.</summary>
public sealed class DeviceGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether user consent is required before viewing.</summary>
    public bool ConsentRequired { get; set; }

    /// <summary>Whether unattended access is allowed.</summary>
    public bool UnattendedAllowed { get; set; } = true;

    public string? Note { get; set; }

    public ICollection<Device> Devices { get; set; } = [];
}

/// <summary>A managed device. Anchor for per-device identity and approval state.</summary>
public sealed class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable agent-side device identifier, also used as certificate CN.</summary>
    public string DeviceId { get; set; } = string.Empty;

    public string Hostname { get; set; } = string.Empty;

    public Guid? GroupId { get; set; }
    public DeviceGroup? Group { get; set; }

    public DeviceStatus Status { get; set; } = DeviceStatus.Pending;

    /// <summary>Device-level override for group ConsentRequired. Null inherits from group.</summary>
    public bool? ConsentRequired { get; set; }

    /// <summary>Whether updates may be sent to this device. False = frozen, for example test/quarantine.</summary>
    public bool UpdateAllowed { get; set; } = true;

    /// <summary>Release channel: "rtm" (default) or "beta". Beta devices receive beta channel packages.</summary>
    public string Channel { get; set; } = "rtm";

    /// <summary>Bastion transport for the reverse tunnel: "auto" (443→22 fallback), "ssl443", "ssh22", "wss443".</summary>
    public string BastionTransport { get; set; } = "auto";

    /// <summary>Whether unattended access is allowed. Null inherits from group.</summary>
    public bool? UnattendedAllowed { get; set; }

    /// <summary>Stable unique bastion port for the device reverse tunnel, assigned at enrollment.</summary>
    public int? TunnelPort { get; set; }

    /// <summary>Agent mTLS client certificate thumbprint.</summary>
    public string? CertThumbprint { get; set; }

    /// <summary>Agent SSH public key for bastion authorized_keys/CA flows.</summary>
    public string? SshPublicKey { get; set; }

    /// <summary>Per-device VNC password, encrypted. With loopback-only it gates local access.</summary>
    public string? VncSecret { get; set; }
    public DateTimeOffset? VncSecretUpdatedAt { get; set; }

    // Denormalized from the latest telemetry for fast listing.
    public string? AgentVersion { get; set; }
    public string? HelperVersion { get; set; }
    public string? VncVersion { get; set; }
    public string? ClientVersion { get; set; }
    public string? OsVersion { get; set; }

    /// <summary>System manufacturer / model / serial from SMBIOS (denormalized from telemetry). Null/OEM on generic desktops.</summary>
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Agent restart count and last incident reported by Helper supervisor for observability.</summary>
    public int AgentRestarts { get; set; }
    public string? LastIncident { get; set; }

    /// <summary>Whether remote access was disabled locally on the device (VNC lock). Display only; enforcement is local.</summary>
    public bool VncLocked { get; set; }

    // Detailed telemetry from the latest report, denormalized for display.
    public DateTimeOffset? BootTimeUtc { get; set; }
    public string? IpAddress { get; set; }
    /// <summary>Public IP where the agent connects from, observed from the telemetry request source IP.</summary>
    public string? PublicIpAddress { get; set; }

    /// <summary>Reverse DNS (PTR) for PublicIpAddress, resolved once per IP and cached. Null = not resolved yet; "" = looked up, no PTR.</summary>
    public string? PublicIpReverse { get; set; }

    // Failed-login counter and lockout for device-level brute-force protection.
    public int LoginFailCount { get; set; }
    /// <summary>When not null, sign-in from this device is locked after 5 failures. Admin-only unlock.</summary>
    public DateTimeOffset? LoginLockedAt { get; set; }
    public string? WifiSsid { get; set; }
    public bool VpnActive { get; set; }
    public string? LoggedInUser { get; set; }

    /// <summary>Power (denormalized from telemetry): on AC/charger, battery % (null = no battery / desktop),
    /// and the sleep (standby) idle timeout in minutes on AC / on battery (0 = never, null = unknown).</summary>
    public bool AcOnline { get; set; }
    public int? BatteryPercent { get; set; }
    public int? SleepAcMinutes { get; set; }
    public int? SleepDcMinutes { get; set; }

    public DateTimeOffset EnrolledAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Admin note such as user name, stored encrypted because it may be sensitive.</summary>
    public string? Note { get; set; }
}

/// <summary>
/// A change worth remembering about a device: when it moved between online / flaky / not-controllable /
/// offline, and when its IP changed. Written only when a value actually changes.
///
/// This replaced a per-minute telemetry snapshot table that grew to 755 MB across fifteen devices in three
/// months - and which no endpoint or client ever read. Everything current lives denormalised on
/// <see cref="Device"/>; only the transitions are worth keeping, and a stable device now writes none.
/// </summary>
public sealed class DeviceEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>What changed: <c>state</c>, <c>ip</c> (local) or <c>public-ip</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Previous value; null when this is the first observation.</summary>
    public string? OldValue { get; set; }

    public string? NewValue { get; set; }
}

/// <summary>Event kinds stored in <see cref="DeviceEvent.Kind"/>. Kept as constants so the writer, the
/// history endpoint and the console agree on the vocabulary.</summary>
public static class DeviceEventKinds
{
    public const string State = "state";
    public const string Ip = "ip";
    public const string PublicIp = "public-ip";
}
