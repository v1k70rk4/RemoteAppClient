using OtpNet;

namespace RemoteServer.Security;

/// <summary>
/// TOTP 2FA (RFC 6238, SHA-1 / 6 digits / 30s), matching standard authenticator apps.
/// Secret is generated as base32; caller encrypts it at rest with SecretProtector.
/// </summary>
public static class TotpService
{
    /// <summary>New random TOTP secret in base32 form with 20 bytes of entropy.</summary>
    public static string GenerateSecret() =>
        Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));

    /// <summary>otpauth:// URI for QR code, with issuer and account name.</summary>
    public static string BuildUri(string secretBase32, string account, string issuer)
    {
        string Enc(string s) => Uri.EscapeDataString(s);
        return $"otpauth://totp/{Enc(issuer)}:{Enc(account)}" +
               $"?secret={secretBase32}&issuer={Enc(issuer)}&algorithm=SHA1&digits=6&period=30";
    }

    /// <summary>Validates code with +/-1 time window for clock skew tolerance.</summary>
    public static bool Verify(string secretBase32, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        try
        {
            var totp = new Totp(Base32Encoding.ToBytes(secretBase32));
            return totp.VerifyTotp(code.Trim(), out _, new VerificationWindow(previous: 1, future: 1));
        }
        catch { return false; }
    }
}
