using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteAgent.Telemetry;

namespace RemoteAgent.Security;

/// <summary>
/// The device's mTLS key as a named, machine-scoped, non-exportable CNG key: in the TPM through the Microsoft
/// Platform Crypto Provider when the TPM is usable, otherwise in the software key storage provider. Either way
/// the key signs in place and never leaves the machine as bytes; the certificate sits in LocalMachine\My bound
/// to it, and SChannel uses the pair for client authentication (verified on real hardware: TLS 1.3, ECDSA P-256).
/// ECDSA rather than RSA because TPM RSA-PSS salt lengths and TLS 1.3 do not agree on every firmware, and the
/// device CA is P-256 anyway.
/// </summary>
public static class DeviceKeyStore
{
    public const string Tpm = "tpm";
    public const string Software = "software";
    public const string File = "file";

    private const string TpmProviderName = "Microsoft Platform Crypto Provider";
    private const string SoftwareProviderName = "Microsoft Software Key Storage Provider";

    public sealed record NewKey(ECDsa Key, string Provider, string KeyName) : IDisposable
    {
        public void Dispose() => Key.Dispose();
    }

    /// <summary>The TPM can take the key: present, ready for storage, and not flagged for vulnerable firmware.</summary>
    public static bool TpmUsable()
    {
        var t = TpmInfo.Read();
        return t.Present == true && t.Ready == true && t.VulnerableFirmware != true;
    }

    /// <summary>Creates a fresh key, in the TPM when asked and possible, otherwise in software. Throws only when
    /// even the software provider refuses (the caller then falls back to the PFX file).</summary>
    public static NewKey Create(bool preferTpm)
    {
        var name = "RemoteAgent-Device-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));
        if (preferTpm)
        {
            try { return Make(name, TpmProviderName, Tpm); }
            catch (CryptographicException) { /* TPM refused (cleared, owned elsewhere, policy): software it is */ }
        }
        return Make(name, SoftwareProviderName, Software);
    }

    private static NewKey Make(string name, string providerName, string tag)
    {
        var p = new CngKeyCreationParameters
        {
            Provider = new CngProvider(providerName),
            KeyCreationOptions = CngKeyCreationOptions.MachineKey,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
        };
        var key = CngKey.Create(CngAlgorithm.ECDsaP256, name, p);
        return new NewKey(new ECDsaCng(key), tag, name);
    }

    /// <summary>Puts the issued certificate into LocalMachine\My bound to the key. Returns the thumbprint.</summary>
    public static string InstallCertificate(string certificatePem, ECDsa key)
    {
        using var leaf = X509Certificate2.CreateFromPem(certificatePem);
        using var withKey = leaf.CopyWithPrivateKey(key);
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(withKey);
        return withKey.Thumbprint;
    }

    /// <summary>Removes a certificate from LocalMachine\My. Best effort.</summary>
    public static void RemoveCertificate(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) return;
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            foreach (var c in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false))
                store.Remove(c);
        }
        catch { /* best effort */ }
    }

    /// <summary>Deletes a named key from whichever provider holds it. Best effort.</summary>
    public static void DeleteKey(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return;
        foreach (var provider in new[] { TpmProviderName, SoftwareProviderName })
        {
            try
            {
                var p = new CngProvider(provider);
                if (CngKey.Exists(keyName, p, CngKeyOpenOptions.MachineKey))
                    CngKey.Open(keyName, p, CngKeyOpenOptions.MachineKey).Delete();
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>Whether the named key is still there (a cleared TPM takes its keys with it).</summary>
    public static bool KeyExists(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return false;
        foreach (var provider in new[] { TpmProviderName, SoftwareProviderName })
        {
            try { if (CngKey.Exists(keyName, new CngProvider(provider), CngKeyOpenOptions.MachineKey)) return true; }
            catch { /* provider missing on this machine */ }
        }
        return false;
    }
}
