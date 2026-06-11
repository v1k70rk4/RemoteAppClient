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
