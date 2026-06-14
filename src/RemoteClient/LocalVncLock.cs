using System.Diagnostics;
using Microsoft.Win32;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient;

/// <summary>
/// Manages the local device VNC lock from the client: reads the flag, then toggles it
/// through the agent executable elevated by UAC. The privileged operation is performed
/// by the agent (vnc-lock/vnc-unlock); the client only launches it. This keeps the lock
/// local-admin-only and not remotely disableable.
/// </summary>
public static class LocalVncLock
{
    public static bool IsLocked()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\RemoteAppClient");
            return k?.GetValue("VncLocked") is int v && v != 0;
        }
        catch { return false; }
    }

    /// <summary>Agent executable path from the RemoteAgent service ImagePath.</summary>
    public static string? ResolveAgentExe()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\RemoteAgent");
            if (k?.GetValue("ImagePath") is not string img || string.IsNullOrWhiteSpace(img)) return null;

            var path = img.Trim();
            if (path.StartsWith('"')) { int e = path.IndexOf('"', 1); if (e > 1) path = path[1..e]; }
            else { int x = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); if (x > 0) path = path[..(x + 4)]; }
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    /// <summary>Runs vnc-lock / vnc-unlock elevated by UAC. True when the process exits with 0.</summary>
    public static bool RunElevated(bool lockIt)
    {
        var exe = ResolveAgentExe()
                  ?? throw new InvalidOperationException(L.LocalVncLock_001);
        var psi = new ProcessStartInfo(exe, lockIt ? "vnc-lock" : "vnc-unlock") { UseShellExecute = true, Verb = "runas" };
        using var p = Process.Start(psi);
        if (p is null) return false;
        p.WaitForExit();
        return p.ExitCode == 0;
    }
}
