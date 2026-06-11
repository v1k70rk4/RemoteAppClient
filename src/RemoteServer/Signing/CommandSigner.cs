using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using RemoteAgent.Commands;
using RemoteServer.Configuration;

namespace RemoteServer.Signing;

/// <summary>
/// Aláírt parancsokat állít elő a szerver privát kulcsával. A tényleges aláírás a
/// KÖZÖS <see cref="CommandSignature"/>-rel történik (Contracts) — így a kliens
/// ellenőrzése és a szerver aláírása definíció szerint egyezik.
/// </summary>
public sealed class CommandSigner : IDisposable
{
    private readonly ECDsa _privateKey;

    public CommandSigner(IOptions<ServerOptions> options)
    {
        var path = options.Value.CommandSigningKeyPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException(
                $"A parancs-aláíró privát kulcs nem található: '{path}'. Állítsd be a Server:CommandSigningKeyPath-ot.");

        _privateKey = ECDsa.Create();
        _privateKey.ImportFromPem(File.ReadAllText(path));
    }

    /// <summary>Új, friss nonce-szal és időbélyeggel ellátott, aláírt parancs.</summary>
    public AgentCommand Create(string type, CommandData? data = null)
    {
        var cmd = new AgentCommand
        {
            Type = type,
            Nonce = Guid.NewGuid().ToString("N"),
            IssuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Data = data,
        };
        CommandSignature.Sign(cmd, _privateKey);
        return cmd;
    }

    public void Dispose() => _privateKey.Dispose();
}
