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

    /// <summary>A bástya (reverse SSH tunnel) elérési adatai. Az enroll válaszába kerül.</summary>
    public BastionOptions Bastion { get; set; } = new();
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
}
