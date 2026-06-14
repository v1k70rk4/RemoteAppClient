using System.Text.Json.Serialization;

namespace RemoteAgent.Enrollment;

/// <summary>
/// Enrollment result stored locally by the agent in enrollment.json.
/// Run mode reads its own identity, client-cert thumbprint, and server access from this.
/// The private key plus cert live in the PFX; the CA certificate is stored separately.
/// </summary>
public sealed class EnrollmentRecord
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("certThumbprint")]
    public string CertThumbprint { get; set; } = string.Empty;

    [JsonPropertyName("caPinSha256")]
    public string CaPinSha256 { get; set; } = string.Empty;

    /// <summary>Server command-signing public key (Base64 SPKI) used to verify commands.</summary>
    [JsonPropertyName("commandSigningPublicKey")]
    public string CommandSigningPublicKey { get; set; } = string.Empty;

    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = string.Empty;

    // Bastion access for the reverse tunnel, received from the server during enrollment.
    [JsonPropertyName("bastionHost")]
    public string BastionHost { get; set; } = string.Empty;

    [JsonPropertyName("bastionPort")]
    public int BastionPort { get; set; }

    [JsonPropertyName("bastionUser")]
    public string BastionUser { get; set; } = string.Empty;

    [JsonPropertyName("bastionHostKey")]
    public string BastionHostKey { get; set; } = string.Empty;

    [JsonPropertyName("enrolledAtUtc")]
    public DateTimeOffset EnrolledAtUtc { get; set; }
}

/// <summary>Source-generated JSON for agent-local, non-wire types without reflection.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(EnrollmentRecord))]
public sealed partial class AgentLocalJsonContext : JsonSerializerContext;
