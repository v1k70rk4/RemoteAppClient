using System.Runtime.InteropServices;

namespace RemoteAgent.Telemetry;

/// <summary>
/// Event-driven AC/charger state via the Win32 power-setting notification (GUID_ACDC_POWER_SOURCE). This is
/// reliable in a Session-0 service, where <c>GetSystemPowerStatus.ACLineStatus</c> can read stale (it kept
/// reporting "on AC" after unplug, while the battery percent updated correctly). The notification delivers
/// the current source on registration and again on every plug/unplug, so we both get a correct
/// <see cref="AcOnline"/> and can fire <see cref="Changed"/> to send telemetry immediately (no message pump,
/// no window — a callback subscription works inside a service).
/// </summary>
internal static class PowerMonitor
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint DeviceNotifyCallback(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters { public IntPtr Callback; public IntPtr Context; }

    [DllImport("powrprof.dll")] private static extern uint PowerSettingRegisterNotification(ref Guid settingGuid, uint flags, ref DeviceNotifySubscribeParameters recipient, out IntPtr registrationHandle);
    [DllImport("powrprof.dll")] private static extern uint PowerSettingUnregisterNotification(IntPtr registrationHandle);

    private const uint DEVICE_NOTIFY_CALLBACK = 0x2;
    private const uint PBT_POWERSETTINGCHANGE = 0x8013;
    private static Guid _acdcPowerSource = new("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548"); // GUID_ACDC_POWER_SOURCE

    private static readonly DeviceNotifyCallback _cb = OnNotify; // keep the delegate alive for the registration's lifetime
    private static IntPtr _reg;
    private static volatile bool _acOnline = true;
    private static volatile bool _active;

    /// <summary>True once the notification is registered; otherwise the collector falls back to GetSystemPowerStatus.</summary>
    public static bool Active => _active;

    /// <summary>Live AC/charger state from the power-source notification.</summary>
    public static bool AcOnline => _acOnline;

    /// <summary>Raised on the notification thread when the power source flips (plug / unplug).</summary>
    public static event Action? Changed;

    public static void Start()
    {
        if (_active) return;
        try
        {
            var p = new DeviceNotifySubscribeParameters { Callback = Marshal.GetFunctionPointerForDelegate(_cb), Context = IntPtr.Zero };
            if (PowerSettingRegisterNotification(ref _acdcPowerSource, DEVICE_NOTIFY_CALLBACK, ref p, out _reg) == 0)
                _active = true; // registration immediately delivers the current AC/DC state via the callback
        }
        catch { /* leave _active false; SystemInfoCollector falls back to GetSystemPowerStatus */ }
    }

    public static void Stop()
    {
        if (_reg != IntPtr.Zero) { try { PowerSettingUnregisterNotification(_reg); } catch { /* best effort */ } _reg = IntPtr.Zero; }
        _active = false;
    }

    private static uint OnNotify(IntPtr context, uint type, IntPtr setting)
    {
        try
        {
            if (type == PBT_POWERSETTINGCHANGE && setting != IntPtr.Zero)
            {
                // POWERBROADCAST_SETTING = GUID(16) + DataLength(4) + Data(DWORD). 0 = AC, 1 = battery, 2 = short-term/UPS.
                bool ac = Marshal.ReadInt32(setting, 20) == 0;
                if (ac != _acOnline) { _acOnline = ac; Changed?.Invoke(); }
            }
        }
        catch { /* never throw across the native callback boundary */ }
        return 0;
    }
}
