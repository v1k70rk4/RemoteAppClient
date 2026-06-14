using System.Text.Json.Serialization;

namespace RemoteAgent.Enrollment;

/// <summary>
/// Enrollment request: the device sends a CSR created with its own key plus the token.
/// The private key never leaves the device; the server only signs the CSR.
/// </summary>
public sealed class EnrollRequest
{
    /// <summary>One-time enrollment token in raw form; the server hashes it before lookup.</summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>Device certificate signing request (PEM, PKCS#10).</summary>
    [JsonPropertyName("csr")]
    public string Csr { get; set; } = string.Empty;

    /// <summary>Device name for telemetry and listing. The server assigns the device ID.</summary>
    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    /// <summary>Device SSH public key in OpenSSH format for the bastion tunnel. The server signs it as a CA certificate.</summary>
    [JsonPropertyName("sshPublicKey")]
    public string SshPublicKey { get; set; } = string.Empty;
}

/// <summary>Successful enrollment response: server-assigned ID, signed certificate, and CA.</summary>
public sealed class EnrollResponse
{
    /// <summary>Stable server-assigned device ID, also used as the certificate CN.</summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Signed client certificate (PEM).</summary>
    [JsonPropertyName("certificate")]
    public string Certificate { get; set; } = string.Empty;

    /// <summary>CA certificate (PEM); the agent pins this for the server connection.</summary>
    [JsonPropertyName("caCertificate")]
    public string CaCertificate { get; set; } = string.Empty;

    /// <summary>The server command-signing public key (ECDSA P-256, Base64 SPKI).
    /// The agent uses it to verify command signatures.</summary>
    [JsonPropertyName("commandSigningPublicKey")]
    public string CommandSigningPublicKey { get; set; } = string.Empty;

    /// <summary>OpenSSH certificate signed by the CA for the agent SSH key, used by the tunnel.</summary>
    [JsonPropertyName("sshCertificate")]
    public string SshCertificate { get; set; } = string.Empty;

    // Bastion access for the reverse tunnel, from server configuration.
    [JsonPropertyName("bastionHost")]
    public string BastionHost { get; set; } = string.Empty;

    [JsonPropertyName("bastionPort")]
    public int BastionPort { get; set; }

    [JsonPropertyName("bastionUser")]
    public string BastionUser { get; set; } = string.Empty;

    /// <summary>Bastion host key for pinning ("type base64", without comment).</summary>
    [JsonPropertyName("bastionHostKey")]
    public string BastionHostKey { get; set; } = string.Empty;
}

/// <summary>Machine-readable error code; the client localizes it.</summary>
public sealed class EnrollError
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;
}

/// <summary>The device reports its unique VNC password to the server so the admin can connect.</summary>
public sealed class VncSecretReport
{
    [JsonPropertyName("secret")]
    public string Secret { get; set; } = string.Empty;
}
