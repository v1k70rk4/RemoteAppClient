namespace RemoteAgent.Tunnel;

/// <summary>Megosztott, szálbiztos jelzés arról, hogy épp él-e a reverse tunnel.</summary>
public sealed class TunnelState
{
    private volatile bool _active;
    public bool IsActive => _active;
    internal void Set(bool value) => _active = value;
}
