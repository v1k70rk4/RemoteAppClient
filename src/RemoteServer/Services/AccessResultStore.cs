using System.Collections.Concurrent;

namespace RemoteServer.Services;

/// <summary>
/// Short-lived store for tunnel-open/consent outcomes, keyed by command nonce.
/// Opening records context (who, which device), agent reports outcome over WSS (PumpIncoming),
/// and console polls by nonce. In-memory, 2 minute TTL.
/// </summary>
public sealed class AccessResultStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    public sealed record Entry(string Actor, Guid? DeviceId, string Hostname)
    {
        public string? Outcome { get; set; }
        public DateTimeOffset Expires { get; set; } = DateTimeOffset.UtcNow + Ttl;
    }

    private readonly ConcurrentDictionary<string, Entry> _map = new();

    /// <summary>At open time: which actor requested access to which deviceId/hostname, before outcome exists.</summary>
    public void SetPending(string nonce, string actor, Guid? deviceId, string hostname)
    {
        if (string.IsNullOrEmpty(nonce)) return;
        _map[nonce] = new Entry(actor, deviceId, hostname);
        Prune();
    }

    /// <summary>Records outcome received from the agent and returns context for audit when known.</summary>
    public Entry? RecordOutcome(string nonce, string outcome)
    {
        if (string.IsNullOrEmpty(nonce)) return null;
        if (_map.TryGetValue(nonce, out var e)) { e.Outcome = outcome; e.Expires = DateTimeOffset.UtcNow + Ttl; return e; }
        var fresh = new Entry("?", null, "") { Outcome = outcome };
        _map[nonce] = fresh;
        return fresh;
    }

    /// <summary>Gets outcome; null means not available yet and console should keep waiting.</summary>
    public string? Get(string nonce)
    {
        var entry = GetEntry(nonce);
        return entry?.Outcome;
    }

    /// <summary>Gets the full entry for authorization and outcome polling.</summary>
    public Entry? GetEntry(string nonce)
    {
        if (!_map.TryGetValue(nonce, out var e)) return null;
        if (e.Expires < DateTimeOffset.UtcNow) { _map.TryRemove(nonce, out _); return null; }
        return e;
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _map)
            if (kv.Value.Expires < now) _map.TryRemove(kv.Key, out _);
    }
}
