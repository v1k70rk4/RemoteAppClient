using System.Runtime.InteropServices;
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

    /// <summary>
    /// Puts the issued certificate into LocalMachine\My bound to the key, and proves the pair works before
    /// returning the thumbprint. The framework's own binding (CopyWithPrivateKey) is tried first; where it fails
    /// the binding is written by hand. The Platform Crypto Provider on Windows 10 reports a machine key as not
    /// being one, which makes the framework look for it in the wrong place and give up with "keyset does not
    /// exist" - the key is fine, only the lookup is. The hand-written binding names the key, its provider and the
    /// machine scope outright, which is exactly what the framework writes when its lookup succeeds.
    /// </summary>
    public static string InstallCertificate(string certificatePem, ECDsa key)
    {
        if (key is not ECDsaCng { Key: { } cng }) throw new CryptographicException("The device key is not a named CNG key.");
        using var leaf = X509Certificate2.CreateFromPem(certificatePem);
        var thumbprint = leaf.Thumbprint;

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        try
        {
            using var withKey = leaf.CopyWithPrivateKey(key);
            store.Add(withKey);
        }
        catch (CryptographicException)
        {
            store.Add(leaf); // public part only; the binding follows
            using var stored = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
                .OfType<X509Certificate2>().FirstOrDefault()
                ?? throw new CryptographicException("The certificate did not land in the store.");
            var info = new CryptKeyProvInfo
            {
                ContainerName = cng.KeyName ?? throw new CryptographicException("The device key has no name."),
                ProvName = cng.Provider?.Provider ?? TpmProviderName,
                ProvType = 0,            // CNG, not a CAPI provider type
                Flags = CryptMachineKeyset,
                KeySpec = 0,             // CNG keys have no CAPI key spec
            };
            if (!CertSetCertificateContextProperty(stored.Handle, CertKeyProvInfoPropId, 0, ref info))
            {
                var err = Marshal.GetLastWin32Error();
                RemoveCertificate(thumbprint);
                throw new CryptographicException($"Binding the certificate to the key failed (0x{err:X8}).");
            }
        }
        store.Close();

        // The only test that counts: load it back the way SChannel will, and sign with it.
        try
        {
            using var check = CertHelper.LoadClientCertificate(thumbprint);
            using var priv = check.GetECDsaPrivateKey() ?? throw new CryptographicException("The stored certificate has no usable private key.");
            var data = RandomNumberGenerator.GetBytes(32);
            var sig = priv.SignData(data, HashAlgorithmName.SHA256);
            if (!key.VerifyData(data, sig, HashAlgorithmName.SHA256)) throw new CryptographicException("The stored certificate's key is not the device key.");
        }
        catch
        {
            RemoveCertificate(thumbprint);
            throw;
        }
        return thumbprint;
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

    /// <summary>Opens a named key that already exists (a recovery request's key across restarts).</summary>
    public static ECDsa? Open(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return null;
        foreach (var provider in new[] { TpmProviderName, SoftwareProviderName })
        {
            try
            {
                var p = new CngProvider(provider);
                if (CngKey.Exists(keyName, p, CngKeyOpenOptions.MachineKey))
                    return new ECDsaCng(CngKey.Open(keyName, p, CngKeyOpenOptions.MachineKey));
            }
            catch { /* provider missing or key unusable: try the next */ }
        }
        return null;
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

    // ---- CERT_KEY_PROV_INFO by hand ---------------------------------------------------------------------

    private const uint CertKeyProvInfoPropId = 2;
    private const uint CryptMachineKeyset = 0x20;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CryptKeyProvInfo
    {
        public string ContainerName;
        public string ProvName;
        public uint ProvType;
        public uint Flags;
        public uint ProvParamCount;
        public IntPtr ProvParams;
        public uint KeySpec;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertSetCertificateContextProperty(IntPtr certContext, uint propId, uint flags, ref CryptKeyProvInfo data);
}
