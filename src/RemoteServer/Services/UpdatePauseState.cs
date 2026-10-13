using System.Collections.Concurrent;

namespace RemoteServer.Services;

/// <summary>
/// Which devices auto-converge has given up on for a while, per component, and what they reported when it did.
/// A device's <c>LastIncident</c> cannot hold this: the device overwrites it with its own incidents (or none)
/// on every telemetry pass. In memory only: a server restart forgets the pauses, which costs each stuck device
/// one more attempt before it is paused again. One pause per device and component: a pass that moves on from a
/// paused updater to the agent package may pause that too without forgetting the first.
///
/// A pause is never final. It lasts <see cref="Duration"/> and then the device gets another attempt (three
/// failures in six hours pause it again): a download that broke on a laptop's sleepy link must not keep that
/// laptop off the update for good.
/// </summary>
public sealed class UpdatePauseState(Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan Duration = TimeSpan.FromHours(6);

    public sealed record Pause(string Version, string Reported, DateTimeOffset Until);

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<(Guid Device, string Component), Pause> _paused = new();

    /// <summary>True when the device is paused for exactly this package while still reporting the same thing
    /// and the pause has not run out. A pause that no longer matches (the report changed, another package) or
    /// has expired is dropped, so the device gets a fresh attempt rather than the stale pause.</summary>
    public bool IsPaused(Guid deviceKey, string component, string version, string reported)
    {
        var key = (deviceKey, component);
        if (!_paused.TryGetValue(key, out var p)) return false;
        if (p.Version == version && p.Reported == reported && _now() < p.Until) return true;
        _paused.TryRemove(new KeyValuePair<(Guid, string), Pause>(key, p));
        return false;
    }

    public void Set(Guid deviceKey, string component, string version, string reported) =>
        _paused[(deviceKey, component)] = new Pause(version, reported, _now() + Duration);

    /// <summary>Forgets every pause of the device.</summary>
    public void Clear(Guid deviceKey)
    {
        foreach (var key in _paused.Keys.Where(k => k.Device == deviceKey).ToList())
            _paused.TryRemove(key, out _);
    }
}
