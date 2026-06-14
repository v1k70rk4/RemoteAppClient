using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Security;

/// <summary>Tanúsítvány-betöltés és szerver-cert pinnelés segédek.</summary>
public static class CertHelper
{
    /// <summary>
    /// Kliens-tanúsítvány (mTLS) betöltése a LocalMachine\My store-ból ujjlenyomat alapján.
    /// A privát kulcsnak elérhetőnek kell lennie a SYSTEM fióknak.
    /// </summary>
    public static X509Certificate2 LoadClientCertificate(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
            throw new InvalidOperationException(L.CertHelper_001);

        var normalized = thumbprint.Replace(" ", "").Replace(":", "").ToUpperInvariant();
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);

        foreach (var cert in store.Certificates)
        {
            if (string.Equals(cert.Thumbprint, normalized, StringComparison.OrdinalIgnoreCase))
                return cert;
        }

        throw new InvalidOperationException(
            L.Format(L.CertHelper_002, normalized));
    }

    /// <summary>
    /// Kliens-tanúsítvány betöltése PFX-fájlból (az enroll-mód ezt írja: agent.pfx).
    /// PersistKeySet kell, mert a Windows SChannel az efemer kulcsot nem tudja kliens-
    /// authhoz használni. A privát kulcs egy kulcs-konténerbe kerül a futtató fiók alatt.
    /// </summary>
    public static X509Certificate2 LoadClientCertificateFromPfx(string path, string? password = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException(L.Format(L.CertHelper_003, path));

        return X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.PersistKeySet);
    }

    /// <summary>DPAPI-védett PFX (.dat) betöltése: visszafejt, majd betölti.</summary>
    public static X509Certificate2 LoadClientCertificateFromProtectedPfx(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException(L.Format(L.CertHelper_004, path));

        var pfxBytes = Dpapi.Unprotect(File.ReadAllBytes(path));
        return X509CertificateLoader.LoadPkcs12(pfxBytes, password: null, X509KeyStorageFlags.PersistKeySet);
    }

    /// <summary>PFX-ből tölt (a .dat DPAPI-védett); ha nincs útvonal, a store-ból ujjlenyomat alapján.</summary>
    public static X509Certificate2 ResolveClientCertificate(string? pfxPath, string? thumbprint)
    {
        if (!string.IsNullOrWhiteSpace(pfxPath))
            return pfxPath.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
                ? LoadClientCertificateFromProtectedPfx(pfxPath)
                : LoadClientCertificateFromPfx(pfxPath);

        return LoadClientCertificate(thumbprint ?? string.Empty);
    }

    /// <summary>
    /// Visszaad egy callbacket, ami CSAK akkor fogadja el a szervert, ha annak
    /// tanúsítvány-ujjlenyomata (SHA-256) megegyezik a pinnelt értékkel.
    /// A lánc/CA érvényessége így másodlagos — a pin a horgony.
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
