using System.Text.Json.Serialization;

namespace RemoteAgent.Telemetry;

/// <summary>Telemetry the agent reports about itself. Shared type: client sends it, server receives it.</summary>
public sealed class TelemetryPayload
{
    [JsonPropertyName("agentId")]
    public string AgentId { get; set; } = string.Empty;

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [JsonPropertyName("osVersion")]
    public string OsVersion { get; set; } = string.Empty;

    [JsonPropertyName("agentVersion")]
    public string AgentVersion { get; set; } = string.Empty;

    // Per-component versions read from binaries on the device. Null = not installed.
    [JsonPropertyName("helperVersion")]
    public string? HelperVersion { get; set; }

    [JsonPropertyName("vncVersion")]
    public string? VncVersion { get; set; }

    [JsonPropertyName("clientVersion")]
    public string? ClientVersion { get; set; }

    [JsonPropertyName("bootTimeUtc")]
    public DateTimeOffset BootTimeUtc { get; set; }

    // Network and signed-in user details for the detailed telemetry view.
    /// <summary>Primary IPv4 address with a gateway, when available.</summary>
    [JsonPropertyName("ipAddress")]
    public string? IpAddress { get; set; }

    /// <summary>Connected Wi-Fi network name (SSID) when on wireless; otherwise null.</summary>
    [JsonPropertyName("wifiSsid")]
    public string? WifiSsid { get; set; }

    /// <summary>Whether a VPN appears active, using tunnel/ppp or known VPN adapter heuristics.</summary>
    [JsonPropertyName("vpnActive")]
    public bool VpnActive { get; set; }

    /// <summary>Interactive user signed in at the device (DOMAIN\\user), when available.</summary>
    [JsonPropertyName("loggedInUser")]
    public string? LoggedInUser { get; set; }

    [JsonPropertyName("collectedAtUtc")]
    public DateTimeOffset CollectedAtUtc { get; set; }

    [JsonPropertyName("tunnelActive")]
    public bool TunnelActive { get; set; }

    /// <summary>Whether remote access was disabled locally on this device (VNC lock).</summary>
    [JsonPropertyName("vncLocked")]
    public bool VncLocked { get; set; }

    // Local Helper supervisor state (supervisor.status), for observability.
    [JsonPropertyName("agentRestarts")]
    public int AgentRestarts { get; set; }

    [JsonPropertyName("lastIncident")]
    public string? LastIncident { get; set; }
}
