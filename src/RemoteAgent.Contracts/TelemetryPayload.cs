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

    // Komponensenkénti verziók (a gépen lévő binárisokból olvasva). Null = nincs telepítve.
    [JsonPropertyName("helperVersion")]
    public string? HelperVersion { get; set; }

    [JsonPropertyName("vncVersion")]
    public string? VncVersion { get; set; }

    [JsonPropertyName("clientVersion")]
    public string? ClientVersion { get; set; }

    [JsonPropertyName("bootTimeUtc")]
    public DateTimeOffset BootTimeUtc { get; set; }

    [JsonPropertyName("collectedAtUtc")]
    public DateTimeOffset CollectedAtUtc { get; set; }

    [JsonPropertyName("tunnelActive")]
    public bool TunnelActive { get; set; }

    /// <summary>Helyileg letiltották-e a távoli elérést (VNC-zár) ezen a gépen.</summary>
    [JsonPropertyName("vncLocked")]
    public bool VncLocked { get; set; }

    // A Helper supervisor lokális állapota (supervisor.status), a megfigyelhetőséghez.
    [JsonPropertyName("agentRestarts")]
    public int AgentRestarts { get; set; }

    [JsonPropertyName("lastIncident")]
    public string? LastIncident { get; set; }
}
