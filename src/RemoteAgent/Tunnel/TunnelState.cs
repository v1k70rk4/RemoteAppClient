namespace RemoteAgent.Tunnel;

/// <summary>Shared, thread-safe signal indicating whether the reverse tunnel is currently active, and whether it
/// carries the VNC forward (a file-only tunnel does not). Both go out in the telemetry: the server keeps a
/// session's VNC password past the hour only while the device reports the VNC forward up. The answer is read
/// off the tunnel itself at the time of asking (<c>live</c>: is its ssh still running?), not off a flag set
/// when it came up: an ssh that the bastion dropped would otherwise be reported up, VNC forward and all, until
/// the next open or close - and keep a password past the hour for a session that is over.</summary>
public sealed class TunnelState
{
    private volatile bool _active, _vnc;
    private volatile Func<bool>? _live;

    public bool IsActive => _active && (_live?.Invoke() ?? true);
    public bool IsVncActive => IsActive && _vnc;

    /// <param name="live">Whether the tunnel this state describes is still running; null for "as set".</param>
    internal void Set(bool active, bool vnc = false, Func<bool>? live = null) { _active = active; _vnc = vnc; _live = active ? live : null; }
}
