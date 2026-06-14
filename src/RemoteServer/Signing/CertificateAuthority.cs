using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using RemoteServer.Configuration;
using L = RemoteServer.Localization.Strings;

namespace RemoteServer.Signing;

/// <summary>
/// Mini CA that signs client certificates. Loads the CA and uses only the public key
/// from CSR. The server assigns the device ID (certificate CN), so devices cannot spoof identity.
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
                L.Format(L.CertificateAuthority_CAFilesAreMissing, opt.CaCertPath, opt.CaKeyPath) +
                L.CertificateAuthority_GenerateThemDuringProvisioningDeploy);

        _caCert = X509Certificate2.CreateFromPemFile(opt.CaCertPath, opt.CaKeyPath);
        logger.LogInformation(L.CertificateAuthority_CALoadedSubject, _caCert.Subject);
    }

    /// <summary>CA certificate in PEM; agents pin this.</summary>
    public string CaCertificatePem => _caCert.ExportCertificatePem();

    /// <summary>
    /// Signs a client CSR for the given server-assigned device ID.
    /// Returns the leaf certificate PEM.
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

        // Allow -5 minutes clock skew, but not before the CA notBefore.
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
