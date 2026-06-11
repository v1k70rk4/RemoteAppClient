using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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
            throw new InvalidOperationException("Nincs kliens-tanúsítvány ujjlenyomat konfigurálva.");

        var normalized = thumbprint.Replace(" ", "").Replace(":", "").ToUpperInvariant();
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);

        foreach (var cert in store.Certificates)
        {
            if (string.Equals(cert.Thumbprint, normalized, StringComparison.OrdinalIgnoreCase))
                return cert;
        }

        throw new InvalidOperationException(
            $"A(z) {normalized} ujjlenyomatú kliens-tanúsítvány nem található a LocalMachine\\My store-ban.");
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
