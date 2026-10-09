using System.Collections.Concurrent;

namespace RemoteServer.Services;

/// <summary>
/// Which devices auto-converge has given up on, and what they reported when it did. A device's
/// <c>LastIncident</c> cannot hold this: the device overwrites it with its own incidents (or none) on every
/// telemetry pass. In memory only: a server restart forgets the pauses, which costs each stuck device one
/// more attempt before it is paused again.
/// </summary>
public sealed class UpdatePauseState
{
    public sealed record Pause(string Component, string Version, string Reported);

    private readonly ConcurrentDictionary<Guid, Pause> _paused = new();

    /// <summary>True when the device is paused for exactly this package while still reporting the same thing.
    /// A pause that no longer matches (the report changed, another package) is dropped, so a device that later
    /// returns to its old report gets a fresh attempt rather than the stale pause.</summary>
    public bool IsPaused(Guid deviceKey, string component, string version, string reported)
    {
        if (!_paused.TryGetValue(deviceKey, out var p)) return false;
        if (p == new Pause(component, version, reported)) return true;
        _paused.TryRemove(new KeyValuePair<Guid, Pause>(deviceKey, p));
        return false;
    }

    public void Set(Guid deviceKey, string component, string version, string reported) =>
        _paused[deviceKey] = new Pause(component, version, reported);

    public void Clear(Guid deviceKey) => _paused.TryRemove(deviceKey, out _);
}
