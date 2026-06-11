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
