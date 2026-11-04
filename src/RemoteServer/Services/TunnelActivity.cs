using System.Collections.Concurrent;

namespace RemoteServer.Services;

/// <summary>
/// What each device last said about its tunnel, and whether a VNC session was opened on it. The agent's
/// telemetry (every minute) carries whether its reverse tunnel is up; it is kept here, in memory, for the one
/// decision that needs it: the session's VNC password is not rotated under a running VNC session. The password
/// is chosen for an hour (VncSessionPassword), and the first VNC open past the hour chose a new one - which the
/// agent sets by restarting the device's VNC server, under the operator whose session from before the hour was
/// still running. A device whose VNC session's tunnel is up keeps its password, however old; the next open
/// after the tunnel has closed chooses the new one.
///
/// The tunnel alone is not the session: the agent reports a file-only tunnel the same way, and a password past
/// the hour must not ride a file session into the next VNC open. So a VNC open is recorded here too
/// (<see cref="VncOpened"/>), and the password is kept only while the device has reported its tunnel up since
/// that open and not down after it. The report goes stale after <see cref="Fresh"/> (a device gone quiet counts
/// as closed), and a device reporting its tunnel down is forgotten at once, VNC open and all.
/// </summary>
public sealed class TunnelActivity
{
    public static readonly TimeSpan Fresh = DeviceLiveness.FreshWindow;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUp = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _vncOpened = new(StringComparer.Ordinal);

    public void Report(string deviceId, bool tunnelUp, DateTimeOffset now)
    {
        if (tunnelUp) _lastUp[deviceId] = now;
        else
        {
            _lastUp.TryRemove(deviceId, out _);
            _vncOpened.TryRemove(deviceId, out _);
        }
    }

    /// <summary>A VNC open-tunnel, with the session password, has gone out to the device.</summary>
    public void VncOpened(string deviceId, DateTimeOffset now) => _vncOpened[deviceId] = now;

    /// <summary>The device reported its tunnel up within <see cref="Fresh"/> of <paramref name="now"/>.</summary>
    public bool IsUp(string deviceId, DateTimeOffset now) =>
        _lastUp.TryGetValue(deviceId, out var at) && at <= now && now - at < Fresh;

    /// <summary>A VNC session is running on the device: a VNC open went out, and the device has reported its
    /// tunnel up since (and not down after it).</summary>
    public bool IsVncSessionUp(string deviceId, DateTimeOffset now) =>
        IsUp(deviceId, now)
        && _vncOpened.TryGetValue(deviceId, out var opened)
        && _lastUp.TryGetValue(deviceId, out var up) && up >= opened;
}
