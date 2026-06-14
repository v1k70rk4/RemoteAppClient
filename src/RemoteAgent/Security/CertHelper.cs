using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Security;

/// <summary>Helpers for certificate loading and server certificate pinning.</summary>
public static class CertHelper
{
    /// <summary>
    /// Loads a client certificate for mTLS from LocalMachine\My by thumbprint.
    /// The private key must be available to the SYSTEM account.
    /// </summary>
    public static X509Certificate2 LoadClientCertificate(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
            throw new InvalidOperationException(L.CertHelper_NoClientCertificateThumbprintConfigured);

        var normalized = thumbprint.Replace(" ", "").Replace(":", "").ToUpperInvariant();
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);

        foreach (var cert in store.Certificates)
        {
            if (string.Equals(cert.Thumbprint, normalized, StringComparison.OrdinalIgnoreCase))
                return cert;
        }

        throw new InvalidOperationException(
            L.Format(L.CertHelper_ClientCertificateWithThumbprintWas, normalized));
    }

    /// <summary>
    /// Loads a client certificate from a PFX file; enroll mode writes agent.pfx.
    /// PersistKeySet is required because Windows SChannel cannot use the ephemeral key for
    /// client authentication. The private key is placed into a key container under the running account.
    /// </summary>
    public static X509Certificate2 LoadClientCertificateFromPfx(string path, string? password = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException(L.Format(L.CertHelper_ClientCertificatePFXNotFound, path));

        return X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.PersistKeySet);
    }

    /// <summary>Loads a DPAPI-protected PFX (.dat) by decrypting and importing it.</summary>
    public static X509Certificate2 LoadClientCertificateFromProtectedPfx(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException(L.Format(L.CertHelper_DPAPIProtectedPFXNotFound, path));

        var pfxBytes = Dpapi.Unprotect(File.ReadAllBytes(path));
        return X509CertificateLoader.LoadPkcs12(pfxBytes, password: null, X509KeyStorageFlags.PersistKeySet);
    }

    /// <summary>Loads from PFX (DPAPI-protected .dat); if no path is set, falls back to store thumbprint.</summary>
    public static X509Certificate2 ResolveClientCertificate(string? pfxPath, string? thumbprint)
    {
        if (!string.IsNullOrWhiteSpace(pfxPath))
            return pfxPath.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
                ? LoadClientCertificateFromProtectedPfx(pfxPath)
                : LoadClientCertificateFromPfx(pfxPath);

        return LoadClientCertificate(thumbprint ?? string.Empty);
    }

    /// <summary>
    /// Returns a callback that accepts the server only when its
    /// certificate fingerprint (SHA-256) matches the pinned value.
    /// Chain/CA validity is secondary here; the pin is the trust anchor.
    /// </summary>
    public static RemoteCertificateValidationCallback PinnedServerValidator(string pinSha256)
    {
        var expected = pinSha256.Replace(" ", "").Replace(":", "").ToUpperInvariant();
        return (_, cert, _, _) =>
        {
            if (cert is null) return false;
            var actual = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        };
    }
}
