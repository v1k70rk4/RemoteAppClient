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
        /// <summary>The answer arrived before the request was bound to the nonce; actor and device are not known yet.</summary>
        public bool Placeholder { get; init; }
    }

    private readonly ConcurrentDictionary<string, Entry> _map = new();

    /// <summary>At open time: which actor requested access to which deviceId/hostname. Returns the entry; when
    /// the agent's answer got here first (a placeholder holds it), the returned entry already carries the outcome.</summary>
    public Entry SetPending(string nonce, string actor, Guid? deviceId, string hostname)
    {
        var entry = new Entry(actor, deviceId, hostname);
        if (string.IsNullOrEmpty(nonce)) return entry;
        // Never overwrite an answer that beat us: merge the placeholder's outcome into the bound entry. Losing
        // it left the console polling into a timeout for an access the device had in fact granted.
        entry = _map.AddOrUpdate(nonce, entry,
            (_, existing) => existing.Placeholder ? new Entry(actor, deviceId, hostname) { Outcome = existing.Outcome } : entry);
        Prune();
        return entry;
    }

    /// <summary>Records outcome received from the agent and returns context for audit when known.</summary>
    public Entry? RecordOutcome(string nonce, string outcome)
    {
        if (string.IsNullOrEmpty(nonce)) return null;
        if (_map.TryGetValue(nonce, out var e)) { e.Outcome = outcome; e.Expires = DateTimeOffset.UtcNow + Ttl; return e; }
        // No request bound yet: a device on a fast link answers before the delivery's bookkeeping is done.
        // Park the answer under a placeholder; SetPending merges it and the request side audits it.
        var fresh = new Entry("?", null, "") { Outcome = outcome, Placeholder = true };
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
