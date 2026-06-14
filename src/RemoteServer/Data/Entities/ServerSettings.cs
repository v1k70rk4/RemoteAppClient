namespace RemoteServer.Data.Entities;

/// <summary>
/// Server-level singleton settings: branding (owner + support) and email sending
/// through SMTP or MS Graph app-only. Secrets (SMTP password, Graph secret) are stored
/// encrypted with SecretProtector.
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
    /// <summary>SMTP password encrypted with SecretProtector.</summary>
    public string? SmtpPasswordEnc { get; set; }

    // MS Graph (O365) app-only
    public string? GraphTenantId { get; set; }
    public string? GraphClientId { get; set; }
    /// <summary>Mailbox UPN/email used as sender.</summary>
    public string? GraphSender { get; set; }
    /// <summary>Graph client secret encrypted with SecretProtector.</summary>
    public string? GraphClientSecretEnc { get; set; }

    /// <summary>Graph client secret expiry, max 2 years. Null = unknown.</summary>
    public DateTimeOffset? GraphSecretExpiresAt { get; set; }

    /// <summary>When the last "expires soon" warning was sent, once per expiry.</summary>
    public DateTimeOffset? SecretExpiryNotifiedAt { get; set; }
}
