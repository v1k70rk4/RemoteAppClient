using System.Security.Cryptography;
using System.Text;

namespace RemoteAgent.Vnc;

/// <summary>
/// vnc.secret: the password the device's VNC server is set to, at rest under DPAPI (machine scope; the agent
/// runs as SYSTEM). Read by the provisioning watchdog, which keeps the VNC server hardened to it, and written
/// by whoever sets a new password - provisioning at first install, and an open-tunnel that brings a session
/// password - so the watchdog never puts an older one back. A legacy plaintext file is migrated on first read.
/// </summary>
public static class VncSecretFile
{
    public const string Name = "vnc.secret";

    public static string PathIn(string enrollmentDir) => Path.Combine(enrollmentDir, Name);

    public static string? Read(string file)
    {
        if (!File.Exists(file)) return null;
        var raw = File.ReadAllBytes(file);
        if (raw.Length == 0) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(raw, null, DataProtectionScope.LocalMachine)).Trim(); }
        catch
        {
            var plain = Encoding.UTF8.GetString(raw).Trim();   // legacy plaintext
            try { Write(file, plain); } catch { /* best-effort migration */ }
            return plain;
        }
    }

    public static void Write(string file, string password) =>
        File.WriteAllBytes(file, ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.LocalMachine));
}
