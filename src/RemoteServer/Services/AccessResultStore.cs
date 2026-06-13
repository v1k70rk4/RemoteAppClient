using System.Collections.Concurrent;

namespace RemoteServer.Services;

/// <summary>
/// A tunnel-nyitás/hozzájárulás kimenetelének rövid életű tárolója, a parancs nonce-ához kötve.
/// Az agent a WSS-en visszajelzi az eredményt (PumpIncoming), a konzol pedig nonce alapján pollozza.
/// Memóriában, 2 perc TTL (egyetlen szerver-instance).
/// </summary>
public sealed class AccessResultStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, (string Outcome, DateTimeOffset Expires)> _map = new();

    /// <summary>Az agenttől érkezett kimenetel rögzítése (nonce → outcome).</summary>
    public void Set(string nonce, string outcome)
    {
        if (string.IsNullOrEmpty(nonce)) return;
        _map[nonce] = (outcome, DateTimeOffset.UtcNow + Ttl);
        Prune();
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
