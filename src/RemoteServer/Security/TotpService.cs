using OtpNet;

namespace RemoteServer.Security;

/// <summary>
/// TOTP 2FA (RFC 6238, SHA-1 / 6 jegy / 30s — a standard authenticator appok ezt várják).
/// A titok base32 stringként készül; nyugalmi tárolásnál a hívó TITKOSÍTJA (SecretProtector).
/// </summary>
public static class TotpService
{
    /// <summary>Új, véletlen TOTP-titok base32 formában (20 bájt entrópia).</summary>
    public static string GenerateSecret() =>
        Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));

    /// <summary>otpauth:// URI a QR-kódhoz (issuer + fióknév).</summary>
    public static string BuildUri(string secretBase32, string account, string issuer)
    {
        string Enc(string s) => Uri.EscapeDataString(s);
        return $"otpauth://totp/{Enc(issuer)}:{Enc(account)}" +
               $"?secret={secretBase32}&issuer={Enc(issuer)}&algorithm=SHA1&digits=6&period=30";
    }

    /// <summary>Kód ellenőrzése ±1 időablakkal (óracsúszás-tűrés).</summary>
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
