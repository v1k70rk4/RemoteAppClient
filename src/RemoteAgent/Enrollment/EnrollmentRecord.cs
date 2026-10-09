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

    /// <summary>Where the private key lives: "tpm" | "software" (named CNG key, certificate in LocalMachine\My)
    /// or "file" (agent.pfx.dat). Missing in records from agents before 2.3 = "file".</summary>
    [JsonPropertyName("keyProvider")]
    public string? KeyProvider { get; set; }

    /// <summary>The CNG key's name for "tpm"/"software", so a re-key can delete the old one.</summary>
    [JsonPropertyName("keyName")]
    public string? KeyName { get; set; }

    /// <summary>Certificate expiry, kept here so it is known without loading the certificate.</summary>
    [JsonPropertyName("certNotAfterUtc")]
    public DateTimeOffset? CertNotAfterUtc { get; set; }
}

/// <summary>Source-generated JSON for agent-local, non-wire types without reflection.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(EnrollmentRecord))]
[JsonSerializable(typeof(RemoteAgent.Services.RekeyService.RecoveryState))]
[JsonSerializable(typeof(RemoteAgent.Services.RekeyService.PendingCandidate))]
public sealed partial class AgentLocalJsonContext : JsonSerializerContext;
