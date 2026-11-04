using System.Collections.Concurrent;

namespace RemoteServer.Services;

/// <summary>
/// What each device last said about its tunnel. The agent's telemetry (every minute) carries whether its reverse
/// tunnel is up; it is kept here, in memory, for the one decision that needs it: the session's VNC password is
/// not rotated under a running session. The password is chosen for an hour (VncSessionPassword), and the first
/// VNC open past the hour chose a new one - which the agent sets by restarting the device's VNC server, under
/// the operator whose session from before the hour was still running. A device whose tunnel is up keeps its
/// password, however old; the next open after the tunnel has closed chooses the new one. The report goes stale
/// after <see cref="Fresh"/> (a device gone quiet counts as closed), and a device reporting its tunnel down is
/// forgotten at once.
/// </summary>
public sealed class TunnelActivity
{
    public static readonly TimeSpan Fresh = DeviceLiveness.FreshWindow;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUp = new(StringComparer.Ordinal);

    public void Report(string deviceId, bool tunnelUp, DateTimeOffset now)
    {
        if (tunnelUp) _lastUp[deviceId] = now;
        else _lastUp.TryRemove(deviceId, out _);
    }

    /// <summary>The device reported its tunnel up within <see cref="Fresh"/> of <paramref name="now"/>.</summary>
    public bool IsUp(string deviceId, DateTimeOffset now) =>
        _lastUp.TryGetValue(deviceId, out var at) && at <= now && now - at < Fresh;
}
