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
}
