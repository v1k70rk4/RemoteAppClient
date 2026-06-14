namespace RemoteAgent.Services;

/// <summary>
/// Shared, thread-safe agent state for the local status pipe: whether C2 is connected
/// and when the last server contact occurred. Tunnel state comes from TunnelState,
/// version comes from the assembly. State only, no secrets.
/// </summary>
public sealed class AgentStatusState
{
    private volatile bool _c2Connected;
    private long _lastContactTicks; // DateTimeOffset.UtcNow.UtcTicks, 0 = never

    public bool C2Connected => _c2Connected;

    public DateTimeOffset? LastServerContactUtc
    {
        get
        {
            var t = Interlocked.Read(ref _lastContactTicks);
            return t == 0 ? null : new DateTimeOffset(t, TimeSpan.Zero);
        }
    }

    /// <summary>C2 (WSS) connected/disconnected. Connection also counts as server contact.</summary>
    public void SetC2Connected(bool connected)
    {
        _c2Connected = connected;
        if (connected) MarkServerContact();
    }

    /// <summary>Successful server communication occurred through C2 or telemetry.</summary>
    public void MarkServerContact() =>
        Interlocked.Exchange(ref _lastContactTicks, DateTimeOffset.UtcNow.UtcTicks);
}
