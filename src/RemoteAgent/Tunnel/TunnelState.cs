namespace RemoteAgent.Tunnel;

/// <summary>Shared, thread-safe signal indicating whether the reverse tunnel is currently active.</summary>
public sealed class TunnelState
{
    private volatile bool _active;
    public bool IsActive => _active;
    internal void Set(bool value) => _active = value;
}
