using System.Diagnostics;
using Microsoft.Win32;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient;

/// <summary>
/// A HELYI gép VNC-zárának kezelése a kliensből: a flag olvasása, és a tényleges be/ki
/// kapcsolás az agent exéjén keresztül, EMELT joggal (UAC). A privilegizált műveletet az
/// agent végzi (vnc-lock/vnc-unlock) — a kliens csak elindítja. Így a zár csak helyi
/// admin-jelenléttel (UAC) állítható, távolról nem.
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

    /// <summary>Az agent exe útja a RemoteAgent service ImagePath-jából.</summary>
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

    /// <summary>vnc-lock / vnc-unlock futtatása EMELT joggal (UAC). Igaz, ha a folyamat 0-val tért vissza.</summary>
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
