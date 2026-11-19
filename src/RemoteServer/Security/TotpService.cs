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
    public static bool Verify(string secretBase32, string code) => Verify(secretBase32, code, out _);

    /// <summary>Same, also returning the 30-second time step the code matched, so the caller can refuse a
    /// second use of it (<see cref="TotpReplayGuard"/>).</summary>
    public static bool Verify(string secretBase32, string code, out long step) =>
        Verify(secretBase32, code, DateTime.UtcNow, previousSteps: 1, out step);

    /// <summary>Sign-in check against a given time instead of the clock (tests).</summary>
    internal static bool Verify(string secretBase32, string code, DateTime at, out long step) =>
        Verify(secretBase32, code, at, previousSteps: 1, out step);

    /// <summary>
    /// The first confirmation of a new authenticator: one more previous step than at sign-in, so a code is
    /// still taken for about a minute after the app moved on. Somebody setting up an authenticator for the
    /// first time reads the code off the phone and types it slowly; sign-in keeps the tighter window.
    /// </summary>
    public static bool VerifyEnrollment(string secretBase32, string code, out long step) =>
        Verify(secretBase32, code, DateTime.UtcNow, previousSteps: 2, out step);

    /// <summary>Enrollment check against a given time instead of the clock (tests).</summary>
    internal static bool VerifyEnrollment(string secretBase32, string code, DateTime at, out long step) =>
        Verify(secretBase32, code, at, previousSteps: 2, out step);

    private static bool Verify(string secretBase32, string code, DateTime at, int previousSteps, out long step)
    {
        step = 0;
        if (string.IsNullOrWhiteSpace(code)) return false;
        try
        {
            var totp = new Totp(Base32Encoding.ToBytes(secretBase32));
            return totp.VerifyTotp(at, code.Trim(), out step, new VerificationWindow(previous: previousSteps, future: 1));
        }
        catch { return false; }
    }

    /// <summary>The shortest secret RFC 4226 allows (128 bits); every secret made here is 160.</summary>
    private const int MinSecretBytes = 16;

    /// <summary>
    /// The secret a sign-in hands out while the authenticator is not confirmed yet: the one already on
    /// record when there is one, so an authenticator scanned at an earlier sign-in keeps working (a fresh
    /// secret each time left that entry on the phone producing codes nothing accepts). A new secret only
    /// when there is none, or the stored one cannot be used (not base32, or too short to be a key: "A"
    /// decodes to no bytes at all); <paramref name="isNew"/> says the caller has to store it.
    /// </summary>
    public static string PendingSecret(string? storedSecret, out bool isNew)
    {
        isNew = string.IsNullOrWhiteSpace(storedSecret);
        if (!isNew)
        {
            try
            {
                if (Base32Encoding.ToBytes(storedSecret!).Length >= MinSecretBytes) return storedSecret!;
            }
            catch (ArgumentException) { }
            isNew = true;
        }
        return GenerateSecret();
    }
}
