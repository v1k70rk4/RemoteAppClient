using System.Text.Json.Serialization;

namespace RemoteAgent.Admin;

/// <summary>Bejelentkezés: felhasználónév + jelszó (+ TOTP, ha már be van állítva).</summary>
public sealed class LoginRequest
{
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("password")] public string Password { get; set; } = string.Empty;
    [JsonPropertyName("totp")] public string? Totp { get; set; }
}

/// <summary>
/// Bejelentkezés eredménye. A <see cref="Token"/> a session-token (Bearer).
/// Ha <see cref="MustChangePassword"/> vagy <see cref="TotpEnrollRequired"/> igaz,
/// a kliensnek előbb azt kell elintéznie — addig a konzol-végpontok 403-at adnak.
/// </summary>
public sealed class LoginResponse
{
    [JsonPropertyName("token")] public string Token { get; set; } = string.Empty;
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    [JsonPropertyName("mustChangePassword")] public bool MustChangePassword { get; set; }
    [JsonPropertyName("totpEnrollRequired")] public bool TotpEnrollRequired { get; set; }

    /// <summary>Csak enrollnál: az otpauth:// URI a QR-hez + a base32 titok (kézi beíráshoz).</summary>
    [JsonPropertyName("totpUri")] public string? TotpUri { get; set; }
    [JsonPropertyName("totpSecret")] public string? TotpSecret { get; set; }
}

/// <summary>Hibakód a login/elutasítás kommunikálására (HTTP 401/403 mellé).</summary>
public sealed class AuthError
{
    [JsonPropertyName("error")] public string Error { get; set; } = string.Empty;
}

public sealed class ChangePasswordRequest
{
    [JsonPropertyName("newPassword")] public string NewPassword { get; set; } = string.Empty;
}

public sealed class TotpConfirmRequest
{
    [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
}

// === Windows Hello (passkey-stílus) ===

/// <summary>Hello-hitelesítő regisztrálása (bejelentkezve): a kliens TPM-kulcsának PUBLIKUS része + eszköznév.</summary>
public sealed class HelloRegisterRequest
{
    [JsonPropertyName("publicKey")] public string PublicKey { get; set; } = string.Empty; // base64 SPKI
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = string.Empty;
}

public sealed class HelloRegisterResponse
{
    [JsonPropertyName("credentialId")] public Guid CredentialId { get; set; }
}

/// <summary>Egy regisztrált Hello-eszköz (listázás/visszavonás).</summary>
public sealed class HelloCredentialInfo
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = string.Empty;
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("lastUsedAt")] public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>Belépés 1. lépés: challenge kérése (még nincs session).</summary>
public sealed class HelloChallengeRequest
{
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
}

public sealed class HelloChallengeResponse
{
    [JsonPropertyName("challenge")] public string Challenge { get; set; } = string.Empty; // base64 nonce
}

/// <summary>Belépés 2. lépés: az aláírt challenge.</summary>
public sealed class HelloLoginRequest
{
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("credentialId")] public Guid CredentialId { get; set; }
    [JsonPropertyName("signature")] public string Signature { get; set; } = string.Empty; // base64
}

/// <summary>„Ki vagyok" — a kliens a session-state-hez.</summary>
public sealed class MeResponse
{
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    [JsonPropertyName("mustChangePassword")] public bool MustChangePassword { get; set; }
    [JsonPropertyName("totpConfirmed")] public bool TotpConfirmed { get; set; }
}
