using System.Diagnostics;
using Microsoft.Win32;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Vnc;

/// <summary>
/// Local VNC lock: an admin at the device can disable remote access. It actually stops
/// and disables the tvnserver service, then sets a registry flag respected by the agent
/// so locked devices are not reprovisioned. The server does not need to know; if a tunnel
/// is opened anyway, there is nothing to connect to and the attempt is logged to Windows.
/// Unlocking is local-only because both the flag and service state require local admin/SYSTEM.
/// </summary>
public static class VncLock
{
    private const string LockKey = @"SOFTWARE\RemoteAppClient";
    private const string LockValue = "VncLocked";
    private const string ServiceName = "tvnserver";

    public static bool IsLocked()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(LockKey);
            return k?.GetValue(LockValue) is int v && v != 0;
        }
        catch { return false; }
    }

    /// <summary>CLI: vnc-lock, sets the flag and stops/disables tvnserver. Requires admin/SYSTEM.</summary>
    public static int Lock()
    {
        try
        {
            SetFlag(true);
            RunSc("stop", ServiceName);
            RunSc("config", ServiceName, "start=", "disabled");
            Log(L.VncLock_001);
            Console.WriteLine(L.VncLock_002);
            return 0;
        }
        catch (UnauthorizedAccessException) { Console.Error.WriteLine(L.VncLock_005); return 5; }
        catch (Exception ex) { Console.Error.WriteLine(L.VncLock_006 + ex.Message); return 1; }
    }

    /// <summary>CLI: vnc-unlock, clears the flag and re-enables/starts tvnserver.</summary>
    public static int Unlock()
    {
        try
        {
            SetFlag(false);
            RunSc("config", ServiceName, "start=", "auto");
            RunSc("start", ServiceName);
            Log(L.VncLock_003);
            Console.WriteLine(L.VncLock_004);
            return 0;
        }
        catch (UnauthorizedAccessException) { Console.Error.WriteLine(L.VncLock_005); return 5; }
        catch (Exception ex) { Console.Error.WriteLine(L.VncLock_006 + ex.Message); return 1; }
    }

    /// <summary>Idempotent enforcement: when locked, tvnserver is stopped and remains disabled.</summary>
    public static void Enforce()
    {
        if (!IsLocked()) return;
        try
        {
            RunSc("stop", ServiceName);
            RunSc("config", ServiceName, "start=", "disabled");
        }
        catch { /* best effort */ }
    }

    /// <summary>Writes an event to the Windows log, for example a tunnel attempt against a locked device.</summary>
    public static void Log(string message)
    {
        try { EventLog.WriteEntry("RemoteAgent", message, EventLogEntryType.Warning); }
        catch { /* source may be missing outside admin context */ }
    }

    private static void SetFlag(bool on)
    {
        using var k = Registry.LocalMachine.CreateSubKey(LockKey, writable: true)
                      ?? throw new UnauthorizedAccessException(LockKey);
        k.SetValue(LockValue, on ? 1 : 0, RegistryValueKind.DWord);
    }

    private static void RunSc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
    }
}
