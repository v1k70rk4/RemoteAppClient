namespace RemoteAgent.Security;

/// <summary>
/// The tunnel key the agent presents to the bastion right now. Set at start from enrollment.json and again by a
/// tunnel-key rotation; the tunnels and the in-process ssh-agent read it when they start a connection, so a
/// rotation needs no restart. "tpm" / "software": a named CNG key served by <see cref="Services.SshAgentService"/>,
/// with only the public key and the certificate on disk; "file": the OpenSSH private key file of older
/// enrollments.
/// </summary>
public static class SshIdentity
{
    public sealed record Snapshot(string Provider, string? KeyName, string PublicKeyPath, string CertificatePath, string? PrivateKeyPath)
    {
        /// <summary>The private key is a CNG key behind the agent pipe, not a file.</summary>
        public bool InAgent => Provider is DeviceKeyStore.Tpm or DeviceKeyStore.Software;
    }

    private static Snapshot? _current;
    private static Snapshot? _staged;
    private static int _version;

    public static Snapshot? Current => Volatile.Read(ref _current);

    /// <summary>A key a rotation is proving at the bastion right now: served by the agent beside the current one
    /// for the duration of the probe, and nothing else knows about it.</summary>
    public static Snapshot? Staged
    {
        get => Volatile.Read(ref _staged);
        set { Volatile.Write(ref _staged, value); Interlocked.Increment(ref _version); }
    }

    /// <summary>Bumps on every change, so a cached key handle knows it is stale.</summary>
    public static int Version => Volatile.Read(ref _version);

    public static void Set(Snapshot snapshot)
    {
        Volatile.Write(ref _current, snapshot);
        Interlocked.Increment(ref _version);
    }
}
