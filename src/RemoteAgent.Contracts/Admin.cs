using System.Text.Json.Serialization;

namespace RemoteAgent.Admin;

/// <summary>A device shown in the admin list by client.exe.</summary>
public sealed class DeviceInfo
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("online")]
    public bool Online { get; set; }

    [JsonPropertyName("lastSeenAt")]
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Device VNC password used by the client to generate the viewer .vnc file.</summary>
    [JsonPropertyName("vncSecret")]
    public string? VncSecret { get; set; }

    [JsonPropertyName("groupId")]
    public Guid? GroupId { get; set; }

    [JsonPropertyName("groupName")]
    public string? GroupName { get; set; }

    [JsonPropertyName("updateAllowed")]
    public bool UpdateAllowed { get; set; }

    /// <summary>Release channel: "rtm" or "beta".</summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    /// <summary>Bastion transport: "auto" (443→22 fallback), "ssl443", "ssh22", "wss443". Null = auto.</summary>
    [JsonPropertyName("bastionTransport")]
    public string? BastionTransport { get; set; }

    [JsonPropertyName("unattendedAllowed")]
    public bool? UnattendedAllowed { get; set; }

    [JsonPropertyName("consentRequired")]
    public bool? ConsentRequired { get; set; }

    // Component versions from the latest telemetry, displayed by the client.
    [JsonPropertyName("agentVersion")]
    public string? AgentVersion { get; set; }

    [JsonPropertyName("helperVersion")]
    public string? HelperVersion { get; set; }

    [JsonPropertyName("vncVersion")]
    public string? VncVersion { get; set; }

    [JsonPropertyName("clientVersion")]
    public string? ClientVersion { get; set; }

    /// <summary>True when an update command is in flight (Queued/Sent/Acked) that the device has not applied yet.</summary>
    [JsonPropertyName("updatePending")]
    public bool UpdatePending { get; set; }

    /// <summary>Rollout detail for the pending update, e.g. "agent 1.5.3 · Sent". Null when none.</summary>
    [JsonPropertyName("updatePendingInfo")]
    public string? UpdatePendingInfo { get; set; }

    [JsonPropertyName("osVersion")]
    public string? OsVersion { get; set; }

    /// <summary>System manufacturer / model / serial from SMBIOS. Null/OEM on generic desktops.</summary>
    [JsonPropertyName("manufacturer")] public string? Manufacturer { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("serialNumber")] public string? SerialNumber { get; set; }

    /// <summary>Helper supervisor signals for observability.</summary>
    [JsonPropertyName("agentRestarts")]
    public int AgentRestarts { get; set; }

    [JsonPropertyName("lastIncident")]
    public string? LastIncident { get; set; }

    /// <summary>Whether remote access was disabled locally on the device (VNC lock).</summary>
    [JsonPropertyName("vncLocked")]
    public bool VncLocked { get; set; }

    /// <summary>Admin note, decrypted.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    // Detailed telemetry for the Telemetry tab.
    [JsonPropertyName("bootTimeUtc")] public DateTimeOffset? BootTimeUtc { get; set; }
    [JsonPropertyName("ipAddress")] public string? IpAddress { get; set; }
    /// <summary>Public IP address observed by the server when the agent connects.</summary>
    [JsonPropertyName("publicIpAddress")] public string? PublicIpAddress { get; set; }
    /// <summary>Reverse DNS (PTR) for the public IP, resolved + cached server-side. Null/empty = none (show the IP).</summary>
    [JsonPropertyName("publicIpReverse")] public string? PublicIpReverse { get; set; }
    [JsonPropertyName("wifiSsid")] public string? WifiSsid { get; set; }
    [JsonPropertyName("vpnActive")] public bool VpnActive { get; set; }
    [JsonPropertyName("loggedInUser")] public string? LoggedInUser { get; set; }

    /// <summary>Login lockout after 5 failed attempts from the device. Only an admin can unlock it.</summary>
    [JsonPropertyName("loginFailCount")] public int LoginFailCount { get; set; }
    [JsonPropertyName("loginLocked")] public bool LoginLocked { get; set; }
}

/// <summary>Updates admin-editable device fields (PUT). Null fields are left unchanged.</summary>
public sealed class DeviceUpdate
{
    [JsonPropertyName("groupId")]
    public Guid? GroupId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("updateAllowed")]
    public bool? UpdateAllowed { get; set; }

    [JsonPropertyName("unattendedAllowed")]
    public bool? UnattendedAllowed { get; set; }

    [JsonPropertyName("consentRequired")]
    public bool? ConsentRequired { get; set; }

    /// <summary>Release channel: "rtm" or "beta" (null = unchanged).</summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    /// <summary>Bastion transport: "auto" | "ssl443" | "ssh22" | "wss443" (null = unchanged).</summary>
    [JsonPropertyName("bastionTransport")]
    public string? BastionTransport { get; set; }

    /// <summary>Note stored encrypted by the server.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>
/// Small config blob the server returns in the telemetry response, so it can steer the agent
/// without a separate signed command. Authenticated by the telemetry mTLS channel; the values are
/// non-secret (the bastion host key stays pinned regardless of which port is used).
/// </summary>
public sealed class AgentConfigResponse
{
    /// <summary>Desired bastion transport: "auto" (443→22) | "ssl443" | "ssh22" | "wss443".</summary>
    [JsonPropertyName("bastionTransport")]
    public string? BastionTransport { get; set; }
}

/// <summary>Current package for a channel and component, used by the client channel view.</summary>
public sealed class ChannelPackageInfo
{
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("component")]
    public string Component { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("uploadedAt")]
    public DateTimeOffset UploadedAt { get; set; }
}

/// <summary>Outcome of the last server self-update or rollback (written by the privileged helper).</summary>
public sealed class ServerUpdateResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("at")] public string At { get; set; } = string.Empty;
}

/// <summary>Server self-update state for the "Server update" tab: running version, staged artifacts,
/// last result, and whether a backup exists to roll back to.</summary>
public sealed class ServerUpdateStatus
{
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("stagedTar")] public bool StagedTar { get; set; }
    [JsonPropertyName("stagedTarSize")] public long StagedTarSize { get; set; }
    [JsonPropertyName("stagedSql")] public bool StagedSql { get; set; }
    [JsonPropertyName("lastResult")] public ServerUpdateResult? LastResult { get; set; }
    [JsonPropertyName("backupAvailable")] public bool BackupAvailable { get; set; }
    /// <summary>Whether the privileged self-update helper (deploy.sh + systemd path units) is installed.</summary>
    [JsonPropertyName("helperReady")] public bool HelperReady { get; set; }
}

/// <summary>Device group for the admin list.</summary>
public sealed class GroupInfo
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("consentRequired")]
    public bool ConsentRequired { get; set; }

    [JsonPropertyName("unattendedAllowed")]
    public bool UnattendedAllowed { get; set; }
}

/// <summary>Enrollment/bootstrap token (blob) for the admin list: usage, expiry, and state.</summary>
public sealed class BootstrapTokenInfo
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("groupId")]
    public Guid? GroupId { get; set; }

    [JsonPropertyName("groupName")]
    public string? GroupName { get; set; }

    [JsonPropertyName("maxUses")]
    public int MaxUses { get; set; }

    [JsonPropertyName("useCount")]
    public int UseCount { get; set; }

    [JsonPropertyName("autoApprove")]
    public bool AutoApprove { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("revokedAt")]
    public DateTimeOffset? RevokedAt { get; set; }

    [JsonPropertyName("lastUsedAt")]
    public DateTimeOffset? LastUsedAt { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>Generated MSI file name (/admin/msi/{fileName}) when applicable; null for manual tokens.</summary>
    [JsonPropertyName("msiFileName")]
    public string? MsiFileName { get; set; }
}

/// <summary>Edits an existing blob/token. Null fields are left unchanged.</summary>
public sealed class EditTokenRequest
{
    /// <summary>New maximum install count. Null = unchanged. Rejected when below the already used count.</summary>
    [JsonPropertyName("maxUses")] public int? MaxUses { get; set; }

    /// <summary>New expiry measured in hours from now. Null = unchanged unless clearExpiry is set.</summary>
    [JsonPropertyName("expiresInHours")] public int? ExpiresInHours { get; set; }

    /// <summary>True = no expiry; overrides expiresInHours.</summary>
    [JsonPropertyName("clearExpiry")] public bool ClearExpiry { get; set; }
}

/// <summary>Starts an update command with package version, URL, and SHA-256 hash.</summary>
public sealed class UpdateRequest
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Target component: "agent" (default) or "updater"/"helper".</summary>
    [JsonPropertyName("target")]
    public string? Target { get; set; }
}

/// <summary>Result of open-tunnel: the bastion port allocated by the server.</summary>
public sealed class OpenTunnelResult
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("remotePort")]
    public int RemotePort { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>Nonce of the issued command; the console uses it to poll for the access result.</summary>
    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;
}

/// <summary>An audit log entry. Action is a key such as "connect"; the client localizes it.</summary>
public sealed class AuditEntryInfo
{
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Actor username or "system".</summary>
    [JsonPropertyName("actor")] public string Actor { get; set; } = string.Empty;
    /// <summary>Event key: connect | access-denied | access-timeout | access-no-user | access-locked | user-create | user-update | ...</summary>
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    /// <summary>Affected device hostname or ID for display, when available.</summary>
    [JsonPropertyName("target")] public string? Target { get; set; }
    /// <summary>Optional short human-readable detail, such as a new role or version.</summary>
    [JsonPropertyName("detail")] public string? Detail { get; set; }
}

/// <summary>Access request state polled by the console after opening a tunnel.</summary>
public sealed class AccessResultInfo
{
    /// <summary>"" / "pending" = still waiting; otherwise: auto | granted | denied | timeout | no-user | locked.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;
}

/// <summary>Server-level settings (admin GET/PUT). Secrets are never returned; Has* flags indicate presence.</summary>
public sealed class ServerSettingsInfo
{
    [JsonPropertyName("ownerName")] public string? OwnerName { get; set; }
    [JsonPropertyName("supportPhone")] public string? SupportPhone { get; set; }
    [JsonPropertyName("supportEmail")] public string? SupportEmail { get; set; }

    /// <summary>Language of server-generated system messages/reminders: "auto" (OS), "en" or "hu".</summary>
    [JsonPropertyName("language")] public string Language { get; set; } = "auto";

    /// <summary>"none" | "smtp" | "graph".</summary>
    [JsonPropertyName("emailProvider")] public string EmailProvider { get; set; } = "none";

    [JsonPropertyName("smtpHost")] public string? SmtpHost { get; set; }
    [JsonPropertyName("smtpPort")] public int SmtpPort { get; set; } = 587;
    [JsonPropertyName("smtpUseTls")] public bool SmtpUseTls { get; set; } = true;
    [JsonPropertyName("smtpUser")] public string? SmtpUser { get; set; }
    [JsonPropertyName("smtpFrom")] public string? SmtpFrom { get; set; }
    /// <summary>PUT: empty = unchanged; GET: always null. Presence is indicated by the Has* flag.</summary>
    [JsonPropertyName("smtpPassword")] public string? SmtpPassword { get; set; }
    [JsonPropertyName("hasSmtpPassword")] public bool HasSmtpPassword { get; set; }

    [JsonPropertyName("graphTenantId")] public string? GraphTenantId { get; set; }
    [JsonPropertyName("graphClientId")] public string? GraphClientId { get; set; }
    [JsonPropertyName("graphSender")] public string? GraphSender { get; set; }
    /// <summary>PUT: empty = unchanged; GET: always null.</summary>
    [JsonPropertyName("graphClientSecret")] public string? GraphClientSecret { get; set; }
    [JsonPropertyName("hasGraphSecret")] public bool HasGraphSecret { get; set; }
    /// <summary>Graph client secret expiry time, maximum 2 years from save time.</summary>
    [JsonPropertyName("graphSecretExpiresAt")] public DateTimeOffset? GraphSecretExpiresAt { get; set; }
}

/// <summary>Test email request (admin): sends to the given address with the active provider.</summary>
public sealed class TestEmailRequest
{
    [JsonPropertyName("to")] public string To { get; set; } = string.Empty;
}

/// <summary>Public branding, available before sign-in: owner and support contacts. Sender config is excluded.</summary>
public sealed class BrandingInfo
{
    [JsonPropertyName("ownerName")] public string? OwnerName { get; set; }
    [JsonPropertyName("supportPhone")] public string? SupportPhone { get; set; }
    [JsonPropertyName("supportEmail")] public string? SupportEmail { get; set; }
}
