namespace RemoteAgent.Tunnel;

/// <summary>Shared, thread-safe signal indicating whether the reverse tunnel is currently active, and whether it
/// carries the VNC forward (a file-only tunnel does not). Both go out in the telemetry: the server keeps a
/// session's VNC password past the hour only while the device reports the VNC forward up.</summary>
public sealed class TunnelState
{
    private volatile bool _active, _vnc;
    public bool IsActive => _active;
    public bool IsVncActive => _active && _vnc;
    internal void Set(bool active, bool vnc = false) { _active = active; _vnc = vnc; }
}
