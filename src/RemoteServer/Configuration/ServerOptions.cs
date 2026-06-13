namespace RemoteServer.Configuration;

/// <summary>Szerver-konfiguráció. A titkok (kulcs) fájlból/secretből, nem a repóból.</summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>
    /// A parancs-aláíró ECDSA P-256 PRIVÁT kulcs (PEM) elérési útja. Ennek a
    /// publikus párját pinneli az agent (CommandSigningPublicKey). Soha nem kerül repóba.
    /// </summary>
    public string CommandSigningKeyPath { get; set; } = string.Empty;

    /// <summary>A kliens-certeket aláíró CA tanúsítvány (PEM) útja. Ha nincs, a szerver generál egyet.</summary>
    public string CaCertPath { get; set; } = "/etc/remoteserver/ca.crt";

    /// <summary>A CA privát kulcs (PEM) útja. Csak a service-user olvashatja.</summary>
    public string CaKeyPath { get; set; } = "/etc/remoteserver/ca.key";

    /// <summary>A kiadott kliens-certek érvényessége napokban.</summary>
    public int ClientCertValidityDays { get; set; } = 825;

    /// <summary>A DB-beli titkok (pl. vnc_secret) nyugalmi titkosításához használt 32 bájtos kulcs útja.</summary>
    public string SecretKeyPath { get; set; } = "/etc/remoteserver/secret.key";

    /// <summary>Az update-csomagok tárolási mappája (túléli a redeployt; az /api/updates innen szolgál ki).</summary>
    public string PackagesDir { get; set; } = "/var/lib/remoteserver/packages";

    /// <summary>A szerver publikus bázis-URL-je (pl. https://c2.pelda.hu) — a bootstrap blobba kerül.</summary>
    public string PublicUrl { get; set; } = string.Empty;

    /// <summary>
    /// A LEGRÉGEBBI konzol-kliens verzió, ami beléphet. Az ennél régebbi kliens login-kor
    /// "mustUpdate" választ kap (nincs session), és kötelezően frissül. Üres = nincs korlát.
    /// </summary>
    public string MinClientVersion { get; set; } = "1.1.1.0";

    /// <summary>Az MSI Authenticode-aláírása (opcionális — üres CertPath = nincs aláírás, csak teszt/hobbi).</summary>
    public MsiSigningOptions MsiSigning { get; set; } = new();

    /// <summary>A bástya (reverse SSH tunnel) elérési adatai. Az enroll válaszába kerül.</summary>
    public BastionOptions Bastion { get; set; } = new();
}

/// <summary>
/// MSI Authenticode-aláírás (osslsigncode-dal, Linuxon). Üres CertPath = kihagyva
/// (aláíratlan MSI — SmartScreen figyelmeztet, de Intune-push/silent telepítésnél nem zavar).
/// </summary>
public sealed class MsiSigningOptions
{
    /// <summary>A code-signing tanúsítvány (PFX) útja. Üres = nincs aláírás.</summary>
    public string CertPath { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string TimestampUrl { get; set; } = "http://timestamp.digicert.com";
}

/// <summary>A bástya konfigja. A Host/HostKey gépspecifikus → env/appsettings a boxon, NEM a repóból.</summary>
public sealed class BastionOptions
{
    /// <summary>A bástya publikus hostja, ahova az agent ssh -R-t épít (pl. a szerver domainje).</summary>
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string User { get; set; } = "agent";

    /// <summary>A bástya host-kulcsa pinneléshez ("típus base64", comment nélkül).</summary>
    public string HostKey { get; set; } = string.Empty;

    /// <summary>Az SSH-CA privát kulcs útja (ssh-keygen -s ezzel ír alá). Csak remotesrv olvassa.</summary>
    public string SshCaKeyPath { get; set; } = "/etc/remoteserver/agent_ca";

    /// <summary>A kiadott SSH-certek érvényessége napokban.</summary>
    public int SshCertValidityDays { get; set; } = 825;

    /// <summary>A gépenkénti stabil tunnel-portok tartománya (inkluzív min, exkluzív max).</summary>
    public int TunnelPortMin { get; set; } = 50000;
    public int TunnelPortMax { get; set; } = 60000;
}
