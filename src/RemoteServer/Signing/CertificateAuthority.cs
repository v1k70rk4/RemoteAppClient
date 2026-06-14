using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using RemoteServer.Configuration;
using L = RemoteServer.Localization.Strings;

namespace RemoteServer.Signing;

/// <summary>
/// A kliens-certeket aláíró mini-CA. Betölti a CA-t (vagy első indításkor generál
/// egy önaláírt ECDSA P-256 CA-t). A CSR-ből CSAK a publikus kulcsot használja; a
/// device-azonosítót (a cert CN-jét) a SZERVER osztja — így a gép nem hamisíthat azonosítót.
/// </summary>
public sealed class CertificateAuthority : IDisposable
{
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    private readonly X509Certificate2 _caCert;
    private readonly int _validityDays;
    private readonly object _signLock = new();

    public CertificateAuthority(IOptions<ServerOptions> options, ILogger<CertificateAuthority> logger)
    {
        var opt = options.Value;
        _validityDays = opt.ClientCertValidityDays;

        if (!File.Exists(opt.CaCertPath) || !File.Exists(opt.CaKeyPath))
            throw new InvalidOperationException(
                L.Format(L.CertificateAuthority_001, opt.CaCertPath, opt.CaKeyPath) +
                L.CertificateAuthority_002);

        _caCert = X509Certificate2.CreateFromPemFile(opt.CaCertPath, opt.CaKeyPath);
        logger.LogInformation(L.CertificateAuthority_003, _caCert.Subject);
    }

    /// <summary>A CA tanúsítványa PEM-ben (az agent ezt pinneli).</summary>
    public string CaCertificatePem => _caCert.ExportCertificatePem();

    /// <summary>
    /// Aláír egy kliens-CSR-t a megadott (szerver által osztott) device-azonosítóra.
    /// Visszaadja a leaf cert PEM-jét.
    /// </summary>
    public string SignClientCsr(string csrPem, string deviceId)
    {
        var csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256);

        var leaf = new CertificateRequest(
            new X500DistinguishedName($"CN={deviceId}"), csr.PublicKey, HashAlgorithmName.SHA256);

        leaf.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leaf.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        leaf.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid(ClientAuthOid)], false));
        leaf.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(csr.PublicKey, false));

        // -5 perc óraeltérés-tűrés, de nem lehet korábbi, mint a CA notBefore-ja.
        var caNotBefore = new DateTimeOffset(_caCert.NotBefore.ToUniversalTime());
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        if (notBefore < caNotBefore) notBefore = caNotBefore;
        var notAfter = notBefore.AddDays(_validityDays);
        byte[] serial = RandomNumberGenerator.GetBytes(16);

        lock (_signLock)
        {
            using var cert = leaf.Create(_caCert, notBefore, notAfter, serial);
            return cert.ExportCertificatePem();
        }
    }

    public void Dispose() => _caCert.Dispose();
}
