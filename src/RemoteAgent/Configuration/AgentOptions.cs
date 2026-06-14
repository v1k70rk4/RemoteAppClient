namespace RemoteAgent.Configuration;

/// <summary>
/// Complete agent configuration tree, populated from appsettings.json plus device-specific
/// Intune overrides from registry or environment.
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Stable device identifier. When empty, it is derived from the device SID/MachineGuid.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Enrollment output directory (enrollment.json, agent.pfx, ca.crt).</summary>
    public string EnrollmentDir { get; set; } = @"C:\ProgramData\RemoteAgent";

    /// <summary>Client certificate PFX path for mTLS. Filled from enrollment.json after enrollment.</summary>
    public string ClientCertPfxPath { get; set; } = string.Empty;

    public CommandChannelOptions CommandChannel { get; set; } = new();
    public TunnelOptions Tunnel { get; set; } = new();
    public TelemetryOptions Telemetry { get; set; } = new();
}

/// <summary>Outgoing WSS command channel to the server (mTLS + signed commands).</summary>
public sealed class CommandChannelOptions
{
    /// <summary>For example wss://c2.example.com/agent.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Agent client certificate for mTLS from the CurrentUser/LocalMachine store.</summary>
    public string ClientCertThumbprint { get; set; } = string.Empty;

    /// <summary>Pinned SHA-256 fingerprint of the server TLS certificate.</summary>
    public string ServerCertPinSha256 { get; set; } = string.Empty;

    /// <summary>Server command-signing public key (ECDSA P-256, Base64 SPKI) used to verify commands.</summary>
    public string CommandSigningPublicKey { get; set; } = string.Empty;

    /// <summary>Accepted command timestamp age in seconds, used as the replay window.</summary>
    public int MaxCommandAgeSeconds { get; set; } = 60;

    public int ReconnectBaseDelaySeconds { get; set; } = 2;
    public int ReconnectMaxDelaySeconds { get; set; } = 120;

    /// <summary>WebSocket keepalive ping interval in seconds.</summary>
    public int KeepAliveIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// How long to wait for the keepalive pong before treating the connection as dead.
    /// Without this, the client may miss a post-sleep half-open connection because ReceiveAsync
    /// can block until the OS TCP timeout, often hours. This reconnects after roughly interval+timeout.
    /// </summary>
    public int KeepAliveTimeoutSeconds { get; set; } = 10;
}

/// <summary>SSH reverse tunnel parameters. The target is fixed; the server cannot override it.</summary>
public sealed class TunnelOptions
{
    /// <summary>Bastion/relay server where the agent opens the outbound tunnel.</summary>
    public string BastionHost { get; set; } = string.Empty;
    public int BastionPort { get; set; } = 22;
    public string BastionUser { get; set; } = "agent";

    /// <summary>Agent SSH private key in OpenSSH format, readable only by SYSTEM.</summary>
    public string PrivateKeyPath { get; set; } = string.Empty;

    /// <summary>Agent SSH certificate signed by the bastion CA. Empty means plain key-based auth.</summary>
    public string CertificatePath { get; set; } = string.Empty;

    /// <summary>Pinned bastion host key as a known_hosts line. Required for tunnel creation.</summary>
    public string BastionHostKey { get; set; } = string.Empty;

    /// <summary>Local port exposed through the tunnel, defaulting to the VNC server.</summary>
    public int LocalForwardPort { get; set; } = 5900;

    /// <summary>Automatically closes the tunnel after inactivity.</summary>
    public int IdleTimeoutSeconds { get; set; } = 1800;

    /// <summary>Path to ssh.exe. Empty resolves from PATH.</summary>
    public string SshExecutablePath { get; set; } = @"C:\Windows\System32\OpenSSH\ssh.exe";
}

/// <summary>Sends telemetry to the server-side API over mTLS, never directly to SQL.</summary>
public sealed class TelemetryOptions
{
    /// <summary>For example https://c2.example.com/api/telemetry.</summary>
    public string IngestUrl { get; set; } = string.Empty;

    /// <summary>Same client certificate as the command channel, or a separate one.</summary>
    public string ClientCertThumbprint { get; set; } = string.Empty;

    public string ServerCertPinSha256 { get; set; } = string.Empty;

    public int IntervalSeconds { get; set; } = 300;
}
