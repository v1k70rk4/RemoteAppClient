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

    [JsonPropertyName("unattendedAllowed")]
    public bool? UnattendedAllowed { get; set; }

    [JsonPropertyName("consentRequired")]
    public bool? ConsentRequired { get; set; }

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

    /// <summary>Megjegyzés (a szerver TITKOSÍTVA tárolja).</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }
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
