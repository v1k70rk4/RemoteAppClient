namespace RemoteServer.Data.Entities;

/// <summary>Eszközcsoport. A consent/unattended alapértelmezés csoportszinten dől el.</summary>
public sealed class DeviceGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    /// <summary>Megtekintés előtt kell-e a felhasználó hozzájárulása.</summary>
    public bool ConsentRequired { get; set; }

    /// <summary>Engedélyezett-e az unattended (felügyelet nélküli) hozzáférés.</summary>
    public bool UnattendedAllowed { get; set; } = true;

    public string? Note { get; set; }

    public ICollection<Device> Devices { get; set; } = [];
}

/// <summary>Egy felügyelt gép. A per-device identitás + a jóváhagyási állapot („licenc") horgonya.</summary>
public sealed class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stabil, agent-oldali gépazonosító (a cert CN-je is ez).</summary>
    public string DeviceId { get; set; } = string.Empty;

    public string Hostname { get; set; } = string.Empty;

    public Guid? GroupId { get; set; }
    public DeviceGroup? Group { get; set; }

    public DeviceStatus Status { get; set; } = DeviceStatus.Pending;

    /// <summary>Gép-szintű override a csoport ConsentRequired-jéhez (null = örökli a csoportét).</summary>
    public bool? ConsentRequired { get; set; }

    /// <summary>Mehet-e erre a gépre frissítés. False = befagyasztva (pl. teszt/karantén).</summary>
    public bool UpdateAllowed { get; set; } = true;

    /// <summary>Release-csatorna: "rtm" (alap) vagy "beta". A BETA-gépek a beta csatorna csomagjait kapják.</summary>
    public string Channel { get; set; } = "rtm";

    /// <summary>Engedélyezett-e az unattended hozzáférés (null = örökli a csoportét).</summary>
    public bool? UnattendedAllowed { get; set; }

    /// <summary>A gép stabil, egyedi bástya-portja a reverse tunnelhez (enrollkor kiosztva).</summary>
    public int? TunnelPort { get; set; }

    /// <summary>Az agent mTLS kliens-certjének ujjlenyomata.</summary>
    public string? CertThumbprint { get; set; }

    /// <summary>Az agent SSH publikus kulcsa (a bástya authorized_keys-éhez).</summary>
    public string? SshPublicKey { get; set; }

    /// <summary>Gépenként egyedi VNC-jelszó, TITKOSÍTVA. Loopback-only mellett a helyi hozzáférést zárja.</summary>
    public string? VncSecret { get; set; }
    public DateTimeOffset? VncSecretUpdatedAt { get; set; }

    // Denormalizált, gyors listázáshoz a legutóbbi telemetriából.
    public string? AgentVersion { get; set; }
    public string? HelperVersion { get; set; }
    public string? VncVersion { get; set; }
    public string? ClientVersion { get; set; }
    public string? OsVersion { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>A Helper supervisor által jelzett agent-újraindítások száma + utolsó incidens (megfigyelhetőség).</summary>
    public int AgentRestarts { get; set; }
    public string? LastIncident { get; set; }

    /// <summary>A gépen HELYILEG letiltották-e a távoli elérést (VNC-zár). Csak megjelenítés — a kényszerítés lokális.</summary>
    public bool VncLocked { get; set; }

    public DateTimeOffset EnrolledAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Admin-megjegyzés (használó neve stb.), TITKOSÍTVA tárolva (érzékeny lehet).</summary>
    public string? Note { get; set; }
}

/// <summary>Telemetria-történet, append-only. A nyers payload JSON-ként; retencióval ürül.</summary>
public sealed class DeviceTelemetry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>A teljes TelemetryPayload JSON-je.</summary>
    public string PayloadJson { get; set; } = "{}";
}
