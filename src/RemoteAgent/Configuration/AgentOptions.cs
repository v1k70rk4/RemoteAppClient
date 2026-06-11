namespace RemoteAgent.Configuration;

/// <summary>
/// A teljes agent konfiguráció egy fában. appsettings.json + Intune-os
/// gépspecifikus override (registry / env) tölti.
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Stabil gépazonosító. Üresen hagyva a gép SID/MachineGuid alapján képződik.</summary>
    public string AgentId { get; set; } = string.Empty;

    public CommandChannelOptions CommandChannel { get; set; } = new();
    public TunnelOptions Tunnel { get; set; } = new();
    public TelemetryOptions Telemetry { get; set; } = new();
}

/// <summary>Kimenő WSS parancscsatorna a szerver felé (mTLS + aláírt parancsok).</summary>
public sealed class CommandChannelOptions
{
    /// <summary>pl. wss://c2.pelda.hu/agent</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Az agent kliens-tanúsítványa az mTLS-hez (CurrentUser/LocalMachine store).</summary>
    public string ClientCertThumbprint { get; set; } = string.Empty;

    /// <summary>A szerver TLS-tanúsítványának pinnelt SHA-256 ujjlenyomata. Ennél marad a kapcsolat.</summary>
    public string ServerCertPinSha256 { get; set; } = string.Empty;

    /// <summary>A szerver parancs-aláíró publikus kulcsa (ECDSA P-256, Base64 SPKI). Ezzel ellenőrizzük a parancsokat.</summary>
    public string CommandSigningPublicKey { get; set; } = string.Empty;

    /// <summary>Mennyi órán belüli parancsidőbélyeget fogadunk el (replay-ablak).</summary>
    public int MaxCommandAgeSeconds { get; set; } = 60;

    public int ReconnectBaseDelaySeconds { get; set; } = 2;
    public int ReconnectMaxDelaySeconds { get; set; } = 120;
}

/// <summary>SSH reverse tunnel paraméterei. A cél FIX, a szerver nem írhatja felül.</summary>
public sealed class TunnelOptions
{
    /// <summary>A bástya/relay szerver, ahova az agent kifelé épít tunnelt.</summary>
    public string BastionHost { get; set; } = string.Empty;
    public int BastionPort { get; set; } = 22;
    public string BastionUser { get; set; } = "agent";

    /// <summary>Az agent SSH privát kulcsa (OpenSSH formátum, csak SYSTEM-nek olvasható).</summary>
    public string PrivateKeyPath { get; set; } = string.Empty;

    /// <summary>A bástya host-kulcsa pinnelve (known_hosts sor). Enélkül nem épül tunnel.</summary>
    public string BastionHostKey { get; set; } = string.Empty;

    /// <summary>Helyi port, amit a tunnel kiajánl — alapból a VNC szerver.</summary>
    public int LocalForwardPort { get; set; } = 5900;

    /// <summary>Inaktivitás után a tunnel automatikus lebontása.</summary>
    public int IdleTimeoutSeconds { get; set; } = 1800;

    /// <summary>Az ssh.exe elérési útja. Üresen a PATH-ról oldódik fel.</summary>
    public string SshExecutablePath { get; set; } = @"C:\Windows\System32\OpenSSH\ssh.exe";
}

/// <summary>Telemetria küldése a szerver-oldali API-ba (mTLS), nem közvetlenül SQL-be.</summary>
public sealed class TelemetryOptions
{
    /// <summary>pl. https://c2.pelda.hu/api/telemetry</summary>
    public string IngestUrl { get; set; } = string.Empty;

    /// <summary>Ugyanaz a kliens-cert, mint a parancscsatornán, vagy külön.</summary>
    public string ClientCertThumbprint { get; set; } = string.Empty;

    public string ServerCertPinSha256 { get; set; } = string.Empty;

    public int IntervalSeconds { get; set; } = 300;
}
