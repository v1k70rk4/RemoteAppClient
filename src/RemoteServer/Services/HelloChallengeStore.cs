using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RemoteServer.Services;

/// <summary>
/// Rövid életű, egyszer-használatos Windows Hello belépési challenge-ek (nonce-ok) tárolása,
/// felhasználónévhez kötve. Memóriában (egyetlen szerver-instance); a nonce 2 percig él.
/// </summary>
public sealed class HelloChallengeStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, (byte[] Nonce, DateTimeOffset Expires)> _map = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Új challenge a felhasználóhoz (felülírja a korábbit). A nyers nonce-t adja vissza.</summary>
    public byte[] Issue(string username)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        _map[username] = (nonce, DateTimeOffset.UtcNow + Ttl);
        return nonce;
    }

    /// <summary>A kiadott challenge kivétele (egyszer használatos): visszaadja a nonce-t, ha érvényes, és törli.</summary>
    public byte[]? Consume(string username)
    {
        if (!_map.TryRemove(username, out var entry)) return null;
        return entry.Expires < DateTimeOffset.UtcNow ? null : entry.Nonce;
    }
}
