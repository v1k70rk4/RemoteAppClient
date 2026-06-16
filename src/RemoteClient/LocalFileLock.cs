using System.Diagnostics;
using Microsoft.Win32;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient;

/// <summary>
/// Manages the local file-transfer lock from the client: reads the flag, then toggles it through the
/// agent executable elevated by UAC (file-lock/file-unlock). Mirrors <see cref="LocalVncLock"/> and is
/// independent of the VNC lock; the privileged op runs in the agent, keeping the lock local-admin-only.
/// </summary>
public static class LocalFileLock
{
    public static bool IsLocked()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\RemoteAppClient");
            return k?.GetValue("FileLocked") is int v && v != 0;
        }
        catch { return false; }
    }

    /// <summary>Runs file-lock / file-unlock elevated by UAC. True when the process exits with 0.</summary>
    public static bool RunElevated(bool lockIt)
    {
        var exe = LocalVncLock.ResolveAgentExe()
                  ?? throw new InvalidOperationException(L.LocalVncLock_TheRemoteAgentServiceExecutableWas);
        var psi = new ProcessStartInfo(exe, lockIt ? "file-lock" : "file-unlock") { UseShellExecute = true, Verb = "runas" };
        using var p = Process.Start(psi);
        if (p is null) return false;
        p.WaitForExit();
        return p.ExitCode == 0;
    }
}
