using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Configuration;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Commands;

/// <summary>
/// Eldönti, hogy egy beérkezett parancs valóban a mi szerverünktől jött-e és
/// nem visszajátszott. Két ellenőrzés:
///   1. ECDSA P-256 aláírás a parancs kanonikus formája felett (a szerver pinnelt publikus kulcsával).
///   2. Időablak + egyszer-használatos nonce (replay-védelem).
/// Bármelyik bukik → a parancs eldobva.
/// </summary>
public sealed class CommandVerifier : IDisposable
{
    private readonly ILogger<CommandVerifier> _logger;
    private readonly int _maxAgeSeconds;
    private readonly ECDsa? _publicKey;

    // Látott nonce-ok a replay-ablakon belül: nonce -> lejárat.
    private readonly ConcurrentDictionary<string, long> _seenNonces = new();

    public CommandVerifier(IOptions<AgentOptions> options, ILogger<CommandVerifier> logger)
    {
        _logger = logger;
        var cc = options.Value.CommandChannel;
        _maxAgeSeconds = cc.MaxCommandAgeSeconds;

        if (!string.IsNullOrWhiteSpace(cc.CommandSigningPublicKey))
        {
            _publicKey = ECDsa.Create();
            _publicKey.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(cc.CommandSigningPublicKey), out _);
        }
        else
        {
            _logger.LogWarning(
                L.CommandVerifier_001);
        }
    }

    public bool Verify(AgentCommand cmd)
    {
        if (_publicKey is null)
            return false;

        if (string.IsNullOrEmpty(cmd.Nonce) || string.IsNullOrEmpty(cmd.Signature))
        {
            _logger.LogWarning(L.CommandVerifier_002);
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var age = now - cmd.IssuedAt;
        if (age > _maxAgeSeconds || age < -_maxAgeSeconds)
        {
            _logger.LogWarning(L.CommandVerifier_003, age);
            return false;
        }

        // Aláírás-ellenőrzés a KÖZÖS logikával (Contracts) — nem csúszhat szét a szerverrel.
        if (!CommandSignature.Verify(cmd, _publicKey))
        {
            _logger.LogWarning(L.CommandVerifier_004);
            return false;
        }

        // Replay: a nonce csak egyszer fogadható el az ablakon belül.
        long expiry = now + _maxAgeSeconds;
        if (!_seenNonces.TryAdd(cmd.Nonce, expiry))
        {
            _logger.LogWarning(L.CommandVerifier_005);
            return false;
        }

        PruneNonces(now);
        return true;
    }

    private void PruneNonces(long now)
    {
        foreach (var kvp in _seenNonces)
        {
            if (kvp.Value < now)
                _seenNonces.TryRemove(kvp.Key, out _);
        }
    }

    public void Dispose() => _publicKey?.Dispose();
}
