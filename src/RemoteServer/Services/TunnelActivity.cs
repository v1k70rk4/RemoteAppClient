using System.Collections.Concurrent;

namespace RemoteServer.Services;

/// <summary>
/// What each device last said about its VNC session. The agent's telemetry (every minute) carries whether its
/// reverse tunnel is up and whether that tunnel carries the VNC forward; the latter is kept here, in memory,
/// for the one decision that needs it: the session's VNC password is not rotated under a running VNC session.
/// The password is chosen for an hour (VncSessionPassword), and the first VNC open past the hour chose a new
/// one - which the agent sets by restarting the device's VNC server, under the operator whose session from
/// before the hour was still running. A device reporting its VNC forward up keeps its password, however old;
/// the next open after that report has stopped chooses the new one.
///
/// The evidence is the agent's own, per report: a file-only tunnel reports no VNC forward, so a password past
/// the hour cannot ride a file session into the next VNC open, and a VNC tunnel that gave way to a file tunnel
/// between two reports is told from a running VNC session by the next report. The report goes stale after
/// <see cref="Fresh"/> (a device gone quiet counts as closed), and a device reporting no VNC forward is
/// forgotten at once.
/// </summary>
public sealed class TunnelActivity
{
    public static readonly TimeSpan Fresh = DeviceLiveness.FreshWindow;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastVncUp = new(StringComparer.Ordinal);

    /// <summary>A telemetry report: <paramref name="vncUp"/> is the agent's "tunnel up, with the VNC forward".</summary>
    public void Report(string deviceId, bool vncUp, DateTimeOffset now)
    {
        if (vncUp) _lastVncUp[deviceId] = now;
        else _lastVncUp.TryRemove(deviceId, out _);
    }

    /// <summary>The device reported its VNC forward up within <see cref="Fresh"/> of <paramref name="now"/>.</summary>
    public bool IsVncSessionUp(string deviceId, DateTimeOffset now) =>
        _lastVncUp.TryGetValue(deviceId, out var at) && at <= now && now - at < Fresh;
}
