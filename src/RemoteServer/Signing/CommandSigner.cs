using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using RemoteAgent.Commands;
using RemoteServer.Configuration;
using L = RemoteServer.Localization.Strings;

namespace RemoteServer.Signing;

/// <summary>
/// Creates signed commands with the server private key. Actual signing uses the shared
/// <see cref="CommandSignature"/> from Contracts, so client verification and server signing match by definition.
/// </summary>
public sealed class CommandSigner : IDisposable
{
    private readonly ECDsa _privateKey;

    public CommandSigner(IOptions<ServerOptions> options)
    {
        var path = options.Value.CommandSigningKeyPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException(
                L.Format(L.CommandSigner_001, path));

        _privateKey = ECDsa.Create();
        _privateKey.ImportFromPem(File.ReadAllText(path));
    }

    /// <summary>Public key as Base64 SPKI, sent to agents during enrollment.</summary>
    public string PublicKeySpkiBase64 => Convert.ToBase64String(_privateKey.ExportSubjectPublicKeyInfo());

    /// <summary>New signed command with fresh nonce and timestamp.</summary>
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
