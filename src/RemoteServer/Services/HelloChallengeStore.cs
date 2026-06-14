using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RemoteServer.Services;

/// <summary>
/// Stores short-lived one-time Windows Hello sign-in challenges (nonces), keyed by username.
/// In-memory for a single server instance; nonce lives for 2 minutes.
/// </summary>
public sealed class HelloChallengeStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, (byte[] Nonce, DateTimeOffset Expires)> _map = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a new challenge for a user, replacing any previous one. Returns raw nonce.</summary>
    public byte[] Issue(string username)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        _map[username] = (nonce, DateTimeOffset.UtcNow + Ttl);
        return nonce;
    }

    /// <summary>Consumes a issued one-time challenge: returns nonce when valid, then deletes it.</summary>
    public byte[]? Consume(string username)
    {
        if (!_map.TryRemove(username, out var entry)) return null;
        return entry.Expires < DateTimeOffset.UtcNow ? null : entry.Nonce;
    }
}
