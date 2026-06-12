using System.Text.Json.Serialization;

namespace RemoteAgent.Updater;

/// <summary>
/// A Helper lokális állapota, amit az Agent felolvas és telemetriával felvisz
/// (a Helpernek nincs hálózati jogosultsága). Csak diagnosztika, nem bizalmi adat.
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
