using System.Text.Json.Serialization;

namespace RemoteAgent.Enrollment;

/// <summary>
/// A device asks for a new certificate for a new key, authenticated with the certificate it has now
/// (<c>POST /api/rekey</c>). Used both to move the key into the TPM and to renew a certificate near its end.
/// The server keeps the current certificate valid until the device confirms the new one works
/// (<c>POST /api/rekey/confirm</c>, made with the new certificate), so a device never loses its identity halfway.
/// </summary>
public sealed class RekeyRequest
{
    /// <summary>PKCS#10 signing request (PEM) for the new key. The CN is ignored; the server keeps the device id.</summary>
    [JsonPropertyName("csr")] public string Csr { get; set; } = string.Empty;

    /// <summary>Where the new key lives: "tpm" or "software".</summary>
    [JsonPropertyName("keyProvider")] public string KeyProvider { get; set; } = string.Empty;

    /// <summary>Why: "tpm" (policy: the key belongs in the TPM), "renewal" (certificate near its end), "console".</summary>
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
}

public sealed class RekeyResponse
{
    /// <summary>The new client certificate (PEM), issued for the same device id.</summary>
    [JsonPropertyName("certificate")] public string Certificate { get; set; } = string.Empty;

    /// <summary>Its expiry, so the agent can report it without parsing.</summary>
    [JsonPropertyName("notAfter")] public DateTimeOffset NotAfter { get; set; }

    /// <summary>How long the server keeps the old certificate valid while the device switches.</summary>
    [JsonPropertyName("confirmWithinMinutes")] public int ConfirmWithinMinutes { get; set; }
}

public sealed class RekeyError
{
    [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
}

/// <summary>
/// A device whose key is gone (a cleared TPM) has no certificate to authenticate with. It asks for a new one
/// without a certificate (<c>POST /enroll/rekey</c>); nothing is issued until an administrator approves the
/// request in the console. The device then polls <c>GET /enroll/rekey/{id}?token=</c> for the answer.
/// </summary>
public sealed class RekeyRequestOpen
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("hostname")] public string Hostname { get; set; } = string.Empty;
    [JsonPropertyName("csr")] public string Csr { get; set; } = string.Empty;
    [JsonPropertyName("keyProvider")] public string KeyProvider { get; set; } = string.Empty;
}

public sealed class RekeyRequestOpened
{
    [JsonPropertyName("requestId")] public Guid RequestId { get; set; }
    /// <summary>One-time secret for polling this request; known only to the device that opened it.</summary>
    [JsonPropertyName("token")] public string Token { get; set; } = string.Empty;
}

public sealed class RekeyRequestStatus
{
    /// <summary>"pending" | "approved" | "rejected" | "expired".</summary>
    [JsonPropertyName("state")] public string State { get; set; } = string.Empty;
    [JsonPropertyName("certificate")] public string? Certificate { get; set; }
    [JsonPropertyName("notAfter")] public DateTimeOffset? NotAfter { get; set; }
}
