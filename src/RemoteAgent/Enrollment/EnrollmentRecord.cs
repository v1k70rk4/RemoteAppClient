using System.Text.Json.Serialization;

namespace RemoteAgent.Enrollment;

/// <summary>
/// A beléptetés eredménye, amit az agent helyben eltárol (enrollment.json).
/// A run-mód ebből tudja a saját azonosítóját, a kliens-cert ujjlenyomatát és a
/// szerver-elérést. (A privát kulcs + cert a PFX-ben; a CA cert külön fájlban.)
/// </summary>
public sealed class EnrollmentRecord
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("certThumbprint")]
    public string CertThumbprint { get; set; } = string.Empty;

    [JsonPropertyName("caPinSha256")]
    public string CaPinSha256 { get; set; } = string.Empty;

    /// <summary>A szerver parancs-aláíró publikus kulcsa (Base64 SPKI) — a parancsok ellenőrzéséhez.</summary>
    [JsonPropertyName("commandSigningPublicKey")]
    public string CommandSigningPublicKey { get; set; } = string.Empty;

    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = string.Empty;

    [JsonPropertyName("enrolledAtUtc")]
    public DateTimeOffset EnrolledAtUtc { get; set; }
}

/// <summary>Agent-helyi (nem wire) típusok forrásgenerált JSON-ja — reflection nélkül.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(EnrollmentRecord))]
public sealed partial class AgentLocalJsonContext : JsonSerializerContext;
