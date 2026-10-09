namespace RemoteAgent.Services;

/// <summary>
/// "Reconnect now": a nudge for the command channel from another service. A re-key, and above all a lost-key
/// recovery, changes the certificate every connection must present; without the nudge the channel would sit
/// out whatever backoff its last failures left it with (up to two minutes) before trying with the new one.
/// </summary>
public sealed class ReconnectSignal
{
    public event Action? Requested;
    public void Request() => Requested?.Invoke();
}
