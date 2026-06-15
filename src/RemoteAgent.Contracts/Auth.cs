using System.Text.Json.Serialization;

namespace RemoteAgent.Admin;

/// <summary>Sign-in request: username and password, plus TOTP when already configured.</summary>
public sealed class LoginRequest
{
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("password")] public string Password { get; set; } = string.Empty;
    [JsonPropertyName("totp")] public string? Totp { get; set; }

    /// <summary>Client version; the server rejects outdated clients, see <see cref="LoginResponse.MustUpdate"/>.</summary>
    [JsonPropertyName("clientVersion")] public string? ClientVersion { get; set; }
    /// <summary>Client release channel (rtm/beta); used to select the mandatory update package.</summary>
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    /// <summary>Local agent device ID from the status pipe; used for the device-level failure counter.</summary>
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }

    /// <summary>"Remember this device" trust token from a previous login; lets the server skip TOTP when still valid.</summary>
    [JsonPropertyName("trustToken")] public string? TrustToken { get; set; }

    /// <summary>When true and full 2FA succeeds, the server issues a device-trust token so TOTP can be skipped next time.</summary>
    [JsonPropertyName("rememberDevice")] public bool RememberDevice { get; set; }
}

/// <summary>
/// Sign-in result. <see cref="Token"/> is the bearer session token.
/// When <see cref="MustChangePassword"/> or <see cref="TotpEnrollRequired"/> is true,
/// the client must finish that setup first; console endpoints return 403 until then.
/// </summary>
public sealed class LoginResponse
{
    [JsonPropertyName("token")] public string Token { get; set; } = string.Empty;
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    [JsonPropertyName("mustChangePassword")] public bool MustChangePassword { get; set; }
    [JsonPropertyName("totpEnrollRequired")] public bool TotpEnrollRequired { get; set; }

    /// <summary>Enrollment only: otpauth:// URI for the QR code and the base32 secret for manual entry.</summary>
    [JsonPropertyName("totpUri")] public string? TotpUri { get; set; }
    [JsonPropertyName("totpSecret")] public string? TotpSecret { get; set; }

    /// <summary>
    /// True when the client is too old and must update. In that case <see cref="Token"/> is empty,
    /// and the Update* fields describe the package to download. The client updates and restarts.
    /// </summary>
    [JsonPropertyName("mustUpdate")] public bool MustUpdate { get; set; }
    [JsonPropertyName("updateVersion")] public string? UpdateVersion { get; set; }
    [JsonPropertyName("updateFileName")] public string? UpdateFileName { get; set; }
    [JsonPropertyName("updateSha256")] public string? UpdateSha256 { get; set; }

    /// <summary>Per-operator TightVNC viewer scale ("auto" or a percent "1".."400"). Roams with the account; the client applies it when launching the viewer.</summary>
    [JsonPropertyName("viewerScale")] public string? ViewerScale { get; set; }

    /// <summary>Per-operator TightVNC viewer color depth ("full" or "256"). Roams with the account.</summary>
    [JsonPropertyName("viewerColor")] public string? ViewerColor { get; set; }

    /// <summary>Newly issued "remember this device" token (only when rememberDevice was set and 2FA passed). Store client-side.</summary>
    [JsonPropertyName("trustToken")] public string? TrustToken { get; set; }
}

/// <summary>Error code returned with login or authorization rejection (HTTP 401/403).</summary>
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

// === Windows Hello (passkey-style) ===

/// <summary>Registers a Hello credential while signed in: the public part of the client TPM key plus device name.</summary>
public sealed class HelloRegisterRequest
{
    [JsonPropertyName("publicKey")] public string PublicKey { get; set; } = string.Empty; // base64 SPKI
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = string.Empty;
}

public sealed class HelloRegisterResponse
{
    [JsonPropertyName("credentialId")] public Guid CredentialId { get; set; }
}

/// <summary>A registered Hello device for listing or revocation.</summary>
public sealed class HelloCredentialInfo
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = string.Empty;
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("lastUsedAt")] public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>A remembered (device-trust) machine for admin listing or revocation, with its expiry.</summary>
public sealed class TrustedDeviceInfo
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("deviceName")] public string? DeviceName { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset ExpiresAt { get; set; }
    [JsonPropertyName("lastUsedAt")] public DateTimeOffset LastUsedAt { get; set; }
}

/// <summary>Sign-in step 1: request a challenge before a session exists.</summary>
public sealed class HelloChallengeRequest
{
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
}

public sealed class HelloChallengeResponse
{
    [JsonPropertyName("challenge")] public string Challenge { get; set; } = string.Empty; // base64 nonce
}

/// <summary>Sign-in step 2: submit the signed challenge.</summary>
public sealed class HelloLoginRequest
{
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("credentialId")] public Guid CredentialId { get; set; }
    [JsonPropertyName("signature")] public string Signature { get; set; } = string.Empty; // base64

    /// <summary>Local device id, so a successful Hello sign-in clears that device's failed-login counter.</summary>
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }

    /// <summary>Client version and channel for the minimum-version gate; see <see cref="LoginResponse.MustUpdate"/>.</summary>
    [JsonPropertyName("clientVersion")] public string? ClientVersion { get; set; }
    [JsonPropertyName("channel")] public string? Channel { get; set; }
}

/// <summary>"Who am I" response used by the client session state.</summary>
public sealed class MeResponse
{
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    [JsonPropertyName("mustChangePassword")] public bool MustChangePassword { get; set; }
    [JsonPropertyName("totpConfirmed")] public bool TotpConfirmed { get; set; }

    /// <summary>Per-operator TightVNC viewer scale ("auto" or a percent "1".."400").</summary>
    [JsonPropertyName("viewerScale")] public string? ViewerScale { get; set; }

    /// <summary>Per-operator TightVNC viewer color depth ("full" or "256").</summary>
    [JsonPropertyName("viewerColor")] public string? ViewerColor { get; set; }
}

/// <summary>Updates the signed-in operator's viewer preferences. <see cref="Scale"/> is "auto" or a percent "1".."400".</summary>
public sealed class ViewerPrefsRequest
{
    [JsonPropertyName("scale")] public string? Scale { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }
}
