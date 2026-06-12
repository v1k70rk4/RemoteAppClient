namespace RemoteServer.Data.Entities;

/// <summary>Egyszer-használatos (alapból) beléptető token. Token = jogosultság egy installra.</summary>
public sealed class EnrollmentToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>A token HASH-e (a nyers tokent csak kiadáskor látjuk).</summary>
    public string TokenHash { get; set; } = string.Empty;

    public Guid? CreatedByUserId { get; set; }

    /// <summary>Melyik csoportba kerüljön a beléptetett gép.</summary>
    public Guid? GroupId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }

    public int MaxUses { get; set; } = 1;
    public int UseCount { get; set; }

    /// <summary>
    /// Igaz: a beléptetett gép azonnal Approved (admin által kiadott, egyszer-használatos token).
    /// Hamis: a gép Pending-be kerül, jóváhagyásra vár (site/bootstrap token — önkiszolgáló telepítés).
    /// </summary>
    public bool AutoApprove { get; set; } = true;

    public DateTimeOffset? UsedAt { get; set; }
    public Guid? UsedByDeviceId { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// EGYSÉGES parancs-sor: tunnel, restart, exec, update — mind ide kerül, type-pal.
/// Offline gépnél a parancs Queued állapotban vár, és lefut amint a gép online lesz.
/// </summary>
public sealed class Command
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }

    /// <summary>open-tunnel / close-tunnel / exec / restart / update / …</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Parancs-specifikus paraméterek JSON-ként.</summary>
    public string? PayloadJson { get; set; }

    public CommandStatus Status { get; set; } = CommandStatus.Queued;

    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>A gép által visszaküldött eredmény JSON-ként.</summary>
    public string? ResultJson { get; set; }

    // A kiadott aláírt parancs nonce-a és aláírása (audit + replay-nyomon követés).
    public string? Nonce { get; set; }
    public string? Signature { get; set; }
}

/// <summary>
/// Egy kiadott csomag egy release-csatornán. Csatorna (rtm/beta) + komponens (agent/updater)
/// + verzió. Az adott (csatorna, komponens) "aktuális" csomagja a legfrissebb UploadedAt.
/// A fájl a PackagesDir-ben, az /api/updates/{FileName} szolgálja ki.
/// </summary>
public sealed class ReleasePackage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Release-csatorna: "rtm" (stabil) vagy "beta" (teszt-gyűrű).</summary>
    public string Channel { get; set; } = "rtm";

    /// <summary>Komponens: "agent" vagy "updater".</summary>
    public string Component { get; set; } = "agent";

    public string Version { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Egy távoli megtekintési session — ki, mikor, melyik gépet nézte (privacy-audit).</summary>
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
