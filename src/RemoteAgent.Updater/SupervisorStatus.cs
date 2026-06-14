using System.Text.Json.Serialization;

namespace RemoteAgent.Updater;

/// <summary>
/// Local Helper state read by the Agent and uploaded as telemetry.
/// The Helper has no network authority. Diagnostics only, not trust data.
/// </summary>
public sealed class SupervisorStatus
{
    [JsonPropertyName("updatedAtUtc")] public DateTimeOffset UpdatedAtUtc { get; set; }
    [JsonPropertyName("agentRestarts")] public int AgentRestarts { get; set; }
    [JsonPropertyName("consecutiveFailures")] public int ConsecutiveFailures { get; set; }
    [JsonPropertyName("parked")] public bool Parked { get; set; }
    [JsonPropertyName("lastIncident")] public string? LastIncident { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SupervisorStatus))]
internal sealed partial class SupervisorJson : JsonSerializerContext;
