using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;

namespace RemoteClient;

/// <summary>
/// Windows Hello passkey-style wrapper around KeyCredentialManager. The private key stays
/// in the device TPM and is protected by Hello (fingerprint/face/PIN); only the public key
/// goes to the server. On sign-in, the server challenge is signed after the Hello prompt.
/// </summary>
public static class WindowsHello
{
    /// <summary>Whether Windows Hello is available on this device with PIN or biometrics configured.</summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try { return await KeyCredentialManager.IsSupportedAsync(); }
        catch { return false; }
    }

    /// <summary>Creates a new Hello key for the name and returns the public key as X.509 SPKI base64.</summary>
    public static async Task<string?> CreateAsync(string name)
    {
        AllowForeground(); // let the Hello credential broker come to the front, not behind our window
        var res = await KeyCredentialManager.RequestCreateAsync(name, KeyCredentialCreationOption.ReplaceExisting);
        if (res.Status != KeyCredentialStatus.Success) return null;
        var pub = res.Credential.RetrievePublicKey(CryptographicPublicKeyBlobType.X509SubjectPublicKeyInfo);
        return Convert.ToBase64String(pub.ToArray());
    }

    /// <summary>Signs the challenge with the existing key, triggering the Hello prompt. Returns base64 signature or null.</summary>
    public static async Task<string?> SignAsync(string name, byte[] challenge)
    {
        var open = await KeyCredentialManager.OpenAsync(name);
        if (open.Status != KeyCredentialStatus.Success) return null;
        AllowForeground(); // let the Hello credential broker come to the front, not behind our window
        var sign = await open.Credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(challenge));
        if (sign.Status != KeyCredentialStatus.Success) return null;
        return Convert.ToBase64String(sign.Result.ToArray());
    }

    /// <summary>Whether this device already has a Hello key with the given name.</summary>
    public static async Task<bool> ExistsAsync(string name)
    {
        try { return (await KeyCredentialManager.OpenAsync(name)).Status == KeyCredentialStatus.Success; }
        catch { return false; }
    }

    /// <summary>Deletes the local Hello key for sign-out or unenrollment.</summary>
    public static async Task DeleteAsync(string name)
    {
        try { await KeyCredentialManager.DeleteAsync(name); } catch { /* best effort */ }
    }

    /// <summary>
    /// The Hello prompt is shown by a separate broker process. Because our window holds the foreground,
    /// the OS foreground lock can push that prompt behind us; allow any process to take the foreground so
    /// the credential UI appears on top. Best-effort — ignored if the API is unavailable.
    /// </summary>
    private static void AllowForeground()
    {
        try { AllowSetForegroundWindow(ASFW_ANY); } catch { /* best effort */ }
    }

    private const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
