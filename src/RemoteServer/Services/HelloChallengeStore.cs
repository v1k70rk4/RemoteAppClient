using System.Security.Cryptography;

namespace RemoteServer.Services;

/// <summary>
/// Stores short-lived one-time Windows Hello sign-in challenges (nonces), keyed by username.
/// In-memory for a single server instance; a nonce lives for 2 minutes.
///
/// Anyone may ask for a challenge (the endpoint answers for every name, so it does not reveal who exists), which
/// shapes the limits: a user keeps a few outstanding challenges rather than one, so someone else asking for one
/// in their name cannot replace theirs; signing in uses up only the challenge the signature matches, so a junk
/// attempt uses up nothing; and the whole store has a ceiling, expired entries being swept as it fills.
/// </summary>
public sealed class HelloChallengeStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    private const int PerUser = 4;
    private const int MaxUsers = 10_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, List<(byte[] Nonce, DateTimeOffset Expires)>> _map = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a new challenge for a user (keeping their few newest). Null when the store is full.</summary>
    public byte[]? Issue(string username)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_map.TryGetValue(username, out var list))
            {
                if (_map.Count >= MaxUsers) Sweep(now);
                if (_map.Count >= MaxUsers) return null;
                _map[username] = list = new List<(byte[], DateTimeOffset)>(PerUser);
            }
            list.RemoveAll(c => c.Expires < now);
            if (list.Count >= PerUser) list.RemoveAt(0);
            list.Add((nonce, now + Ttl));
        }
        return nonce;
    }

    /// <summary>Whether the user has any live challenge at all: none means it expired (or never was), which the
    /// console words differently from a signature that matches none.</summary>
    public bool HasLive(string username)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate) return _map.TryGetValue(username, out var list) && list.Exists(c => c.Expires >= now);
    }

    /// <summary>
    /// Uses up the user's live challenge that <paramref name="matches"/> accepts (the signature check) and returns
    /// it; null when none does. Challenges the signature does not match stay for the user's own attempt.
    /// </summary>
    public byte[]? Consume(string username, Func<byte[], bool> matches)
    {
        List<byte[]> candidates;
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_map.TryGetValue(username, out var list)) return null;
            list.RemoveAll(c => c.Expires < now);
            if (list.Count == 0) { _map.Remove(username); return null; }
            candidates = list.Select(c => c.Nonce).ToList();
        }

        // The signature check runs outside the lock; the matching nonce is then taken - once, by whoever gets it.
        foreach (var nonce in candidates)
        {
            if (!matches(nonce)) continue;
            lock (_gate)
            {
                if (!_map.TryGetValue(username, out var list)) return null;
                int i = list.FindIndex(c => ReferenceEquals(c.Nonce, nonce));
                if (i < 0) return null;
                list.RemoveAt(i);
                if (list.Count == 0) _map.Remove(username);
            }
            return nonce;
        }
        return null;
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var key in _map.Keys.ToList())
        {
            var list = _map[key];
            list.RemoveAll(c => c.Expires < now);
            if (list.Count == 0) _map.Remove(key);
        }
    }
}
