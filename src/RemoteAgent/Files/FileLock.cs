using Microsoft.Win32;
using RemoteAgent.Vnc;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Files;

/// <summary>
/// Local file-transfer lock: an admin at the device can disable file transfer independently of VNC.
/// It is just a registry flag the agent honors before starting the file service (there is no service
/// to stop). Set and cleared locally only (admin/SYSTEM), so it cannot be flipped remotely.
/// </summary>
public static class FileLock
{
    private const string LockKey = @"SOFTWARE\RemoteAppClient";
    private const string LockValue = "FileLocked";

    public static bool IsLocked()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(LockKey);
            return k?.GetValue(LockValue) is int v && v != 0;
        }
        catch { return false; }
    }

    /// <summary>CLI: file-lock, sets the flag. Requires admin/SYSTEM. Reuses the VNC lock's event log.</summary>
    public static int Lock() => Set(true, L.FileLock_FileTransferLOCALLYDisabled);

    /// <summary>CLI: file-unlock, clears the flag.</summary>
    public static int Unlock() => Set(false, L.FileLock_FileTransferLOCALLYEnabled);

    private static int Set(bool on, string message)
    {
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(LockKey, writable: true)
                          ?? throw new UnauthorizedAccessException(LockKey);
            k.SetValue(LockValue, on ? 1 : 0, RegistryValueKind.DWord);
            VncLock.Log(message);
            Console.WriteLine(message);
            return 0;
        }
        catch (UnauthorizedAccessException) { Console.Error.WriteLine(L.VncLock_AdminSYSTEMRightsRequired); return 5; }
        catch (Exception ex) { Console.Error.WriteLine(L.VncLock_Error + ex.Message); return 1; }
    }
}
