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
        /// <summary>The outcome is in the audit log already; whoever binds or records next must not file it again.</summary>
        public bool Audited { get; set; }
        /// <summary>The device that sent the outcome (its database id), when the answer came first.</summary>
        public Guid? ReportedBy { get; init; }
    }

    // Answers that arrive before their request are parked, and a device decides what it sends: without a ceiling
    // one device could fill the server's memory with made-up nonces. Real traffic is a handful at a time.
    private const int MaxEntries = 10_000;

    private readonly ConcurrentDictionary<string, Entry> _map = new();

    /// <summary>At open time: which actor requested access to which deviceId/hostname. Returns the entry; when
    /// the agent's answer got here first (a placeholder holds it), the returned entry already carries the outcome.</summary>
    public Entry SetPending(string nonce, string actor, Guid? deviceId, string hostname)
    {
        var entry = new Entry(actor, deviceId, hostname);
        if (string.IsNullOrEmpty(nonce)) return entry;
        // Never overwrite an answer that beat us. The first version of this merged only a placeholder's outcome,
        // but the uplink side may already have bound the nonce from the command row (a fast device, or a queued
        // command answered at the next wake), and the request side's own binding then replaced that entry -
        // outcome and all - so the console polled into a timeout for an access the device had in fact granted.
        // Whatever is already known about the answer stays, whoever binds.
        // An answer parked by a different device than the one this request went to is not the answer: dropped.
        entry = _map.AddOrUpdate(nonce, entry,
            (_, existing) => existing.Outcome is not null && (existing.ReportedBy is null || deviceId is null || existing.ReportedBy == deviceId)
                ? new Entry(actor, deviceId, hostname) { Outcome = existing.Outcome, Audited = existing.Audited, ReportedBy = existing.ReportedBy }
                : entry);
        Prune();
        return entry;
    }

    /// <summary>
    /// Records outcome received from the agent and returns context for audit when known. <paramref name="sender"/>
    /// is the reporting device's database id: an answer for a request that went to another device - or for a nonce
    /// another device already answered - is ignored (null). Null sender skips that check.
    /// </summary>
    public Entry? RecordOutcome(string nonce, string outcome, Guid? sender = null)
    {
        if (string.IsNullOrEmpty(nonce)) return null;
        if (_map.TryGetValue(nonce, out var e))
        {
            if (sender is not null && (e.DeviceId ?? e.ReportedBy) is { } owner && owner != sender) return null;
            e.Outcome = outcome; e.Expires = DateTimeOffset.UtcNow + Ttl; return e;
        }
        // No request bound yet: a device on a fast link answers before the delivery's bookkeeping is done.
        // Park the answer under a placeholder; SetPending merges it and the request side audits it.
        Prune();
        if (_map.Count >= MaxEntries) return null;
        var fresh = new Entry("?", null, "") { Outcome = outcome, Placeholder = true, ReportedBy = sender };
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
