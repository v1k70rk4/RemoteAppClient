using System.Collections.Concurrent;

namespace RemoteServer.Services;

/// <summary>
/// A tunnel-nyitás/hozzájárulás kimenetelének rövid életű tárolója, a parancs nonce-ához kötve.
/// A nyitáskor rögzítjük a kontextust (ki, melyik gép), az agent a WSS-en visszajelzi a kimenetelt
/// (PumpIncoming), a konzol pedig nonce alapján pollozza. Memóriában, 2 perc TTL.
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

    /// <summary>Nyitáskor: ki (actor) melyik gépre (deviceId/hostname) kért hozzáférést — még nincs kimenetel.</summary>
    public void SetPending(string nonce, string actor, Guid? deviceId, string hostname)
    {
        if (string.IsNullOrEmpty(nonce)) return;
        _map[nonce] = new Entry(actor, deviceId, hostname);
        Prune();
    }

    /// <summary>Az agenttől érkezett kimenetel rögzítése; visszaadja a kontextust (audithoz), ha ismert.</summary>
    public Entry? RecordOutcome(string nonce, string outcome)
    {
        if (string.IsNullOrEmpty(nonce)) return null;
        if (_map.TryGetValue(nonce, out var e)) { e.Outcome = outcome; e.Expires = DateTimeOffset.UtcNow + Ttl; return e; }
        var fresh = new Entry("?", null, "") { Outcome = outcome };
        _map[nonce] = fresh;
        return fresh;
    }

    /// <summary>A kimenetel lekérése; null, ha még nincs (a konzol tovább vár).</summary>
    public string? Get(string nonce)
    {
        if (!_map.TryGetValue(nonce, out var e)) return null;
        if (e.Expires < DateTimeOffset.UtcNow) { _map.TryRemove(nonce, out _); return null; }
        return e.Outcome;
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _map)
            if (kv.Value.Expires < now) _map.TryRemove(kv.Key, out _);
    }
}
