using System.Text.Json.Serialization;

namespace RemoteAgent.Telemetry;

/// <summary>Amit az agent magáról jelent. KÖZÖS típus (kliens küldi, szerver fogadja).</summary>
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

    [JsonPropertyName("bootTimeUtc")]
    public DateTimeOffset BootTimeUtc { get; set; }

    [JsonPropertyName("collectedAtUtc")]
    public DateTimeOffset CollectedAtUtc { get; set; }

    [JsonPropertyName("tunnelActive")]
    public bool TunnelActive { get; set; }
}
