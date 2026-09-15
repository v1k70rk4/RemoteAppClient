using RemoteAgent.Admin;
using RemoteServer.Data.Entities;

namespace RemoteServer.Services;

/// <summary>
/// The single definition of what "online" means, shared by the device-list projection and the history
/// watcher, so the badge an operator sees and the transition we record into the history can never drift
/// apart. (They did drift once already: the badge used to come straight from the socket registry, which
/// happily reported a device as online for hours after its wi-fi died.)
/// </summary>
public static class DeviceLiveness
{
    /// <summary>Telemetry lands every 60s, so three intervals separates "alive" from "gone quiet".</summary>
    public static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(3);

    /// <summary>Telemetry arrived recently, whatever the control channel is doing.</summary>
    public static bool IsReporting(Device device, DateTimeOffset now) =>
        device.LastSeenAt > now - FreshWindow;

    /// <summary>
    /// The state exactly as the console renders it. "reporting" is the one worth naming: the agent is alive
    /// and sending telemetry while its control channel is down, so it can be watched but not commanded -
    /// which reads as plain "offline" unless we say otherwise.
    /// </summary>
    public static string State(bool connected, bool reporting, int recentReconnects, string? problem = null) =>
        // A known fault outranks every liveness word: a device can be connected, fresh and green while
        // silently discarding everything we send it, and "online" would be the least useful thing to say.
        !string.IsNullOrWhiteSpace(problem) ? "error"
        : connected && reporting ? "online"
        // A device that has stopped reporting is offline, whatever its link did before it went quiet. With the
        // flaky test first, a machine that was simply shut down - reconnecting a few times on its way out - stayed
        // "flaky" for the whole hour-long reconnect window, as if it were alive on a bad network.
        : !reporting ? "offline"
        : recentReconnects >= DeviceInfo.FlakyReconnectThreshold ? "flaky"
        : "reporting";
}
