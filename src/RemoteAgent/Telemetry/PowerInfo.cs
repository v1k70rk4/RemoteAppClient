using System.Runtime.InteropServices;

namespace RemoteAgent.Telemetry;

/// <summary>
/// Battery status (AC online + charge %) and the active power scheme's sleep (standby) idle timeout for
/// AC and battery, read via Win32 only (no WMI) so it stays NativeAOT-friendly like the other collectors.
/// Desktops report a null battery (no system battery); a null sleep timeout means it could not be read.
/// </summary>
internal static class PowerInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;       // 0 = offline (battery), 1 = online (AC), 255 = unknown
        public byte BatteryFlag;        // bit 7 (128) = no system battery
        public byte BatteryLifePercent; // 0-100, or 255 when unknown
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

    // powrprof.dll: read the active scheme's standby (sleep) idle timeout (in seconds) for AC and battery.
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid powerSettingGuid, out uint acValueIndex);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid powerSettingGuid, out uint dcValueIndex);

    private static readonly Guid GUID_SLEEP_SUBGROUP = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid GUID_STANDBYIDLE = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

    /// <param name="AcOnline">On AC/charger.</param>
    /// <param name="BatteryPercent">0-100, or null on a desktop / when unknown.</param>
    /// <param name="SleepAcMinutes">Standby timeout on AC in minutes (0 = never), or null if unreadable.</param>
    /// <param name="SleepDcMinutes">Standby timeout on battery in minutes (0 = never), or null if unreadable.</param>
    public static (bool AcOnline, int? BatteryPercent, int? SleepAcMinutes, int? SleepDcMinutes) Read()
    {
        bool ac = true;
        int? pct = null;
        try
        {
            if (GetSystemPowerStatus(out var s))
            {
                ac = s.ACLineStatus == 1;
                bool hasBattery = (s.BatteryFlag & 128) == 0;
                if (hasBattery && s.BatteryLifePercent <= 100) pct = s.BatteryLifePercent;
            }
        }
        catch { /* best effort */ }

        int? acMin = null, dcMin = null;
        IntPtr active = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out active) == 0 && active != IntPtr.Zero)
            {
                var scheme = Marshal.PtrToStructure<Guid>(active);
                var sub = GUID_SLEEP_SUBGROUP;
                var setting = GUID_STANDBYIDLE;
                if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint acSec) == 0) acMin = (int)(acSec / 60);
                if (PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint dcSec) == 0) dcMin = (int)(dcSec / 60);
            }
        }
        catch { /* best effort */ }
        finally { if (active != IntPtr.Zero) LocalFree(active); }

        return (ac, pct, acMin, dcMin);
    }
}
