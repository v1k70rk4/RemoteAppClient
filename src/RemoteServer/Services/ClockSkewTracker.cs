using System.Collections.Concurrent;
using RemoteAgent.Admin;

namespace RemoteServer.Services;

/// <summary>
/// Decides whether a device's clock is off, from the CollectedAtUtc stamps its telemetry carries.
/// <para>
/// One stamp is not enough. A laptop that dozes off between collecting the payload and the request reaching
/// us delivers an old stamp when it wakes, and an old stamp looks exactly like a clock that is behind: Dell
/// laptops on Modern Standby did this on every lid-close, and each one was then shown as "clock 34 s behind"
/// until it stayed awake long enough to report again. Delay can only make a stamp look OLDER, never newer,
/// so the freshest of a device's recent stamps is the truest reading of its clock: a clock that is really
/// behind is behind on every report, a sleeping laptop is "behind" on one. A clock that is AHEAD needs no
/// second opinion, since no delay can fake that.
/// </para>
/// </summary>
public sealed class ClockSkewTracker
{
    /// <summary>Half of the agent's 60s command window: warn before commands start being discarded.</summary>
    public const int WarnSeconds = 30;

    /// <summary>Reports arrive a minute apart; a stamp older than this no longer says anything about now.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Samples> _devices = new(StringComparer.Ordinal);

    /// <summary>The device's problem code ("clock-skew:-34") after this report, or null while it looks fine.</summary>
    public string? Problem(string deviceId, DateTimeOffset collectedAt, DateTimeOffset now)
    {
        if (collectedAt == default) return null;                    // old agent that does not send it
        var skew = (collectedAt - now).TotalSeconds;                // + = device ahead of us
        var samples = _devices.GetOrAdd(deviceId, _ => new Samples());
        double best; int count;
        lock (samples) { samples.Add(skew, now); (best, count) = (samples.Best, samples.Count); }

        if (best >= WarnSeconds) return Format(best);               // ahead: one report is proof
        if (best <= -WarnSeconds && count >= 2) return Format(best); // behind: a second report must agree
        return null;
    }

    private static string Format(double skew) => $"{DeviceProblems.ClockSkew}:{skew:+0;-0}";

    /// <summary>The recent (skew, arrival) pairs of one device, pruned to the window on every add.</summary>
    private sealed class Samples
    {
        private const int Keep = 8;                                  // a burst of reports must not grow this
        private readonly List<(double Skew, DateTimeOffset At)> _items = new();

        public void Add(double skew, DateTimeOffset now)
        {
            _items.RemoveAll(s => s.At < now - Window);
            if (_items.Count >= Keep) _items.RemoveAt(0);
            _items.Add((skew, now));
        }

        public int Count => _items.Count;

        /// <summary>The freshest stamp of the window, i.e. the one that suffered the least delay.</summary>
        public double Best => _items.Max(s => s.Skew);
    }
}
