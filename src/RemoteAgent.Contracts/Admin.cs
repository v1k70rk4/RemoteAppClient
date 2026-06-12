using System.Text.Json.Serialization;

namespace RemoteAgent.Admin;

/// <summary>Egy eszköz az admin-listában (a client.exe ezt mutatja).</summary>
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

    /// <summary>A gép VNC-jelszava (a client a viewer .vnc fájljához használja).</summary>
    [JsonPropertyName("vncSecret")]
    public string? VncSecret { get; set; }

    [JsonPropertyName("groupId")]
    public Guid? GroupId { get; set; }

    [JsonPropertyName("groupName")]
    public string? GroupName { get; set; }

    [JsonPropertyName("updateAllowed")]
    public bool UpdateAllowed { get; set; }

    /// <summary>Release-csatorna: "rtm" vagy "beta".</summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("unattendedAllowed")]
    public bool? UnattendedAllowed { get; set; }

    [JsonPropertyName("consentRequired")]
    public bool? ConsentRequired { get; set; }

    // Komponens-verziók a legutóbbi telemetriából (a kliens megjeleníti).
    [JsonPropertyName("agentVersion")]
    public string? AgentVersion { get; set; }

    [JsonPropertyName("helperVersion")]
    public string? HelperVersion { get; set; }

    [JsonPropertyName("vncVersion")]
    public string? VncVersion { get; set; }

    [JsonPropertyName("clientVersion")]
    public string? ClientVersion { get; set; }

    [JsonPropertyName("osVersion")]
    public string? OsVersion { get; set; }

    /// <summary>A Helper supervisor jelzései (megfigyelhetőség).</summary>
    [JsonPropertyName("agentRestarts")]
    public int AgentRestarts { get; set; }

    [JsonPropertyName("lastIncident")]
    public string? LastIncident { get; set; }

    /// <summary>A gépen HELYILEG letiltották-e a távoli elérést (VNC-zár).</summary>
    [JsonPropertyName("vncLocked")]
    public bool VncLocked { get; set; }

    /// <summary>Admin-megjegyzés (visszafejtve).</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>Egy eszköz admin-mezőinek módosítása (PUT). A null mezők változatlanok maradnak.</summary>
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

    /// <summary>Release-csatorna: "rtm" vagy "beta" (null = változatlan).</summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    /// <summary>Megjegyzés (a szerver TITKOSÍTVA tárolja).</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>Egy csatorna aktuális csomagja (komponensenként) — a kliens csatorna-nézetéhez.</summary>
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

/// <summary>Eszközcsoport az admin-listához.</summary>
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

/// <summary>Update-parancs indítása: a csomag verziója, URL-je, SHA-256 hash-e.</summary>
public sealed class UpdateRequest
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Melyik komponens: "agent" (alap) vagy "updater"/"helper".</summary>
    [JsonPropertyName("target")]
    public string? Target { get; set; }
}

/// <summary>Az open-tunnel eredménye: a szerver által kiosztott bástya-port.</summary>
public sealed class OpenTunnelResult
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("remotePort")]
    public int RemotePort { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}
