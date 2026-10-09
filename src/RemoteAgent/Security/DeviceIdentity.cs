using System.Security.Cryptography.X509Certificates;

namespace RemoteAgent.Security;

/// <summary>
/// The device's current certificate identity, shared by every connection the agent makes. Loaded from
/// enrollment.json at start; replaced in place by a re-key, so the next connection of every service picks up
/// the new certificate without a restart (they all resolve it through <see cref="CertHelper"/> per connection).
/// </summary>
public static class DeviceIdentity
{
    private static readonly object Gate = new();
    private static Snapshot? _current;

    /// <summary>Provider "tpm" | "software" | "file"; the store thumbprint for the first two, the PFX path for the last.</summary>
    public sealed record Snapshot(string Provider, string Thumbprint, string? KeyName, string? PfxPath, DateTimeOffset? NotAfter)
    {
        public bool InStore => Provider is DeviceKeyStore.Tpm or DeviceKeyStore.Software;
    }

    public static Snapshot? Current { get { lock (Gate) return _current; } }

    /// <summary>Bumped on every switch, so a long-lived client built for an earlier identity can tell it is stale.</summary>
    public static int Version { get { lock (Gate) return _version; } }
    private static int _version;

    public static void Set(Snapshot s) { lock (Gate) { _current = s; _version++; } }

    /// <summary>Reads the certificate's expiry once, for telemetry and the renewal decision.</summary>
    public static DateTimeOffset? ReadNotAfter(string? pfxPath, string thumbprint)
    {
        try
        {
            using var cert = CertHelper.ResolveClientCertificate(pfxPath, thumbprint);
            return new DateTimeOffset(cert.NotAfter.ToUniversalTime());
        }
        catch { return null; }
    }
}
