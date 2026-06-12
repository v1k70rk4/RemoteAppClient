using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;

namespace RemoteClient;

/// <summary>
/// Windows Hello (passkey-stílus) burkoló a KeyCredentialManager fölött. A privát kulcs a gép
/// TPM-jében, Hello-val (ujjlenyomat/arc/PIN) védve; csak a PUBLIKUS kulcs megy a szerverre.
/// Belépéskor a szerver challenge-ét írjuk alá (ehhez kell a Hello-prompt).
/// </summary>
public static class WindowsHello
{
    /// <summary>Elérhető-e a Windows Hello ezen a gépen (van-e beállítva PIN/biometria)?</summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try { return await KeyCredentialManager.IsSupportedAsync(); }
        catch { return false; }
    }

    /// <summary>Új Hello-kulcs létrehozása a névhez; visszaadja a PUBLIKUS kulcsot (X.509 SPKI, base64).</summary>
    public static async Task<string?> CreateAsync(string name)
    {
        var res = await KeyCredentialManager.RequestCreateAsync(name, KeyCredentialCreationOption.ReplaceExisting);
        if (res.Status != KeyCredentialStatus.Success) return null;
        var pub = res.Credential.RetrievePublicKey(CryptographicPublicKeyBlobType.X509SubjectPublicKeyInfo);
        return Convert.ToBase64String(pub.ToArray());
    }

    /// <summary>A meglévő kulccsal aláírja a challenge-et (kiváltja a Hello-promptot). Aláírás base64, vagy null.</summary>
    public static async Task<string?> SignAsync(string name, byte[] challenge)
    {
        var open = await KeyCredentialManager.OpenAsync(name);
        if (open.Status != KeyCredentialStatus.Success) return null;
        var sign = await open.Credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(challenge));
        if (sign.Status != KeyCredentialStatus.Success) return null;
        return Convert.ToBase64String(sign.Result.ToArray());
    }

    /// <summary>Van-e már Hello-kulcs ezzel a névvel ezen a gépen.</summary>
    public static async Task<bool> ExistsAsync(string name)
    {
        try { return (await KeyCredentialManager.OpenAsync(name)).Status == KeyCredentialStatus.Success; }
        catch { return false; }
    }

    /// <summary>A helyi Hello-kulcs törlése (kijelentkezés / leiratkozás).</summary>
    public static async Task DeleteAsync(string name)
    {
        try { await KeyCredentialManager.DeleteAsync(name); } catch { /* best effort */ }
    }
}
