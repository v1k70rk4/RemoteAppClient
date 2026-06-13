namespace RemoteAgent.Services;

/// <summary>
/// Megosztott, szálbiztos agent-állapot a LOKÁLIS status-pipe-hoz: él-e a C2, mikor volt
/// utolsó szerver-kontakt. A tunnel-állapotot a TunnelState adja, a verziót az assembly.
/// Csak állapot — nincs benne titok.
/// </summary>
public sealed class AgentStatusState
{
    private volatile bool _c2Connected;
    private long _lastContactTicks; // DateTimeOffset.UtcNow.UtcTicks, 0 = még soha

    public bool C2Connected => _c2Connected;

    public DateTimeOffset? LastServerContactUtc
    {
        get
        {
            var t = Interlocked.Read(ref _lastContactTicks);
            return t == 0 ? null : new DateTimeOffset(t, TimeSpan.Zero);
        }
    }

    /// <summary>C2 (WSS) csatlakozott/lecsatlakozott. Csatlakozáskor egyben szerver-kontakt is.</summary>
    public void SetC2Connected(bool connected)
    {
        _c2Connected = connected;
        if (connected) MarkServerContact();
    }

    /// <summary>Sikeres szerver-kommunikáció történt (C2 vagy telemetria).</summary>
    public void MarkServerContact() =>
        Interlocked.Exchange(ref _lastContactTicks, DateTimeOffset.UtcNow.UtcTicks);
}
