namespace RemoteServer.Data.Entities;

/// <summary>
/// Szerver-szintű, egysoros (singleton) beállítások: branding (tulajdonos + support) és
/// e-mail küldés (SMTP vagy MS Graph app-only). A titkok (SMTP jelszó, Graph secret)
/// SecretProtectorral titkosítva tárolódnak.
/// </summary>
public sealed class ServerSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Branding / support
    public string? OwnerName { get; set; }
    public string? SupportPhone { get; set; }
    public string? SupportEmail { get; set; }

    /// <summary>"none" | "smtp" | "graph".</summary>
    public string EmailProvider { get; set; } = "none";

    // SMTP
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseTls { get; set; } = true;
    public string? SmtpUser { get; set; }
    public string? SmtpFrom { get; set; }
    /// <summary>SMTP jelszó, TITKOSÍTVA (SecretProtector).</summary>
    public string? SmtpPasswordEnc { get; set; }

    // MS Graph (O365) app-only
    public string? GraphTenantId { get; set; }
    public string? GraphClientId { get; set; }
    /// <summary>A postafiók (UPN/e-mail), aminek a nevében küld.</summary>
    public string? GraphSender { get; set; }
    /// <summary>Graph client secret, TITKOSÍTVA (SecretProtector).</summary>
    public string? GraphClientSecretEnc { get; set; }

    /// <summary>A Graph client secret lejárati ideje (max 2 év). Null = nem ismert.</summary>
    public DateTimeOffset? GraphSecretExpiresAt { get; set; }

    /// <summary>Mikor küldtünk utoljára „hamarosan lejár" figyelmeztetőt (egyszeri/lejáratonként).</summary>
    public DateTimeOffset? SecretExpiryNotifiedAt { get; set; }
}
