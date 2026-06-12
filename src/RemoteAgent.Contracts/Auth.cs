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

/// <summary>„Ki vagyok" — a kliens a session-state-hez.</summary>
public sealed class MeResponse
{
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    [JsonPropertyName("mustChangePassword")] public bool MustChangePassword { get; set; }
    [JsonPropertyName("totpConfirmed")] public bool TotpConfirmed { get; set; }
}
