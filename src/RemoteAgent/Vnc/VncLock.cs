using System.Diagnostics;
using Microsoft.Win32;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Vnc;

/// <summary>
/// HELYI VNC-zár: egy gépen lévő admin letilthatja a távoli elérést. Ténylegesen LEÁLLÍTJA
/// és LETILTJA a tvnserver service-t, és egy registry-flaget állít, amit az agent tisztel
/// (zárolt állapotban nem provisionál újra). A szervernek nem kell tudnia róla; ha mégis
/// nyitnak tunnelt, nincs mire csatlakozni, és a próbálkozást a Windows-naplóba írjuk.
/// FELOLDANI CSAK HELYBEN lehet (a flag + a service-tiltás helyi admin/SYSTEM jogot kér),
/// ezért TÁVOLRÓL nem kapcsolható ki.
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

    /// <summary>CLI: vnc-lock — flag + a tvnserver leállítása és letiltása. Admin/SYSTEM kell.</summary>
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

    /// <summary>CLI: vnc-unlock — flag törlése + a tvnserver visszaengedése és indítása.</summary>
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

    /// <summary>Idempotens kényszerítés: ha zárolt, a tvnserver biztosan álljon + maradjon disabled.</summary>
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

    /// <summary>Esemény a Windows-naplóba (pl. a zárolt gép elleni tunnel-próbálkozás).</summary>
    public static void Log(string message)
    {
        try { EventLog.WriteEntry("RemoteAgent", message, EventLogEntryType.Warning); }
        catch { /* a source hiányozhat nem-admin kontextusban */ }
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
