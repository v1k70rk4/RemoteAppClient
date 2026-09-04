using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient;

/// <summary>What a device row says about itself, in the order the consoles test for it.</summary>
public enum DeviceState
{
    /// <summary>Enrolled but not yet approved — liveness does not apply.</summary>
    Pending,
    /// <summary>Connected and reporting: it can be commanded.</summary>
    Online,
    /// <summary>Up, but the link keeps dropping (see <see cref="DeviceInfo.RecentReconnects"/>).</summary>
    Flaky,
    /// <summary>Sending telemetry, but its command channel is down: alive, yet not controllable.</summary>
    Reporting,
    /// <summary>Nothing has been heard from it.</summary>
    Offline,
}

/// <summary>
/// One answer to "what state is this device in", shared by both operator consoles.
/// <para>
/// The server decides the underlying flags (see RemoteServer's own <c>DeviceLiveness</c>); this turns them
/// into the single word a human reads. It lives in Core because the two consoles used to disagree about the
/// same device: the Linux one knew only online/offline, so a machine that was reporting but not controllable
/// read "offline" there while the Windows console called it "nem vezérelhető". Colours stay with each UI —
/// only the decision and the wording are shared, which is exactly what was drifting.
/// </para>
/// </summary>
public static class DeviceLiveness
{
    /// <summary>Classifies a device. Order matters: a pending device is not yet a fleet member, and being
    /// connected outranks a flaky history.</summary>
    public static DeviceState Of(DeviceInfo device)
    {
        if (string.Equals(device.Status, "Pending", StringComparison.OrdinalIgnoreCase)) return DeviceState.Pending;
        if (device.Online) return DeviceState.Online;
        if (device.LinkFlaky) return DeviceState.Flaky;
        // Alive and sending telemetry, but its control channel is down: it cannot be connected to, yet it is
        // not switched off either. Keeping this apart from a dark machine is the whole point — otherwise the
        // row reads "offline" while the last-seen column says "just now", which is what confused us.
        if (device.Reporting) return DeviceState.Reporting;
        return DeviceState.Offline;
    }

    /// <summary>The localized label for a state.</summary>
    public static string Label(DeviceState state) => state switch
    {
        DeviceState.Pending => L.DevicesView_StatusPending,
        DeviceState.Online => L.DevicesView_Online,
        DeviceState.Flaky => L.DevicesView_LinkFlaky,
        DeviceState.Reporting => L.DevicesView_ReportingOnly,
        _ => L.DevicesView_Offline,
    };

    /// <summary>The localized label for a device.</summary>
    public static string Label(DeviceInfo device) => Label(Of(device));
}
