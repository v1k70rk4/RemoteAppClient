namespace RemoteAgent.Time;

/// <summary>
/// A nudge from anywhere in the agent that the clock looks wrong and is worth re-checking now.
/// <para>
/// The one caller that matters is <c>CommandVerifier</c>: when a command carries a VALID server signature
/// but a timestamp outside the replay window, the gap is our clock, not a forgery — and that is precisely
/// the state in which no command can reach us any more, so nothing could ever be sent to fix it. The
/// command itself is still refused; only a real time source is ever trusted to set the clock.
/// </para>
/// </summary>
public sealed class TimeSyncTrigger
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>Asks for a sync. Extra calls while one is already pending collapse into it.</summary>
    public void Request()
    {
        try { _signal.Release(); } catch (SemaphoreFullException) { /* already pending */ }
    }

    /// <summary>Waits for a request or the timeout; true when someone asked.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}
