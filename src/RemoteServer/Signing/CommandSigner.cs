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
                L.Format(L.CommandSigner_CommandSigningPrivateKeyNot, path));

        _privateKey = ECDsa.Create();
        _privateKey.ImportFromPem(File.ReadAllText(path));
    }

    /// <summary>Public key as Base64 SPKI, sent to agents during enrollment.</summary>
    public string PublicKeySpkiBase64 => Convert.ToBase64String(_privateKey.ExportSubjectPublicKeyInfo());

    /// <summary>Agents from this version verify the version 2 form (every field, bound to the device).</summary>
    public static readonly Version SigV2MinAgent = new(2, 2, 7, 1);

    /// <summary>New signed command with fresh nonce and timestamp. With <paramref name="deviceId"/> the signature
    /// is version 2, which only agents from <see cref="SigV2MinAgent"/> can check; the caller decides from the
    /// agent's reported version (<see cref="UsesV2"/>) and passes null for older ones.</summary>
    public AgentCommand Create(string type, CommandData? data = null, string? deviceId = null)
    {
        var cmd = new AgentCommand
        {
            Type = type,
            Nonce = Guid.NewGuid().ToString("N"),
            IssuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Data = data,
        };
        CommandSignature.Sign(cmd, _privateKey, deviceId);
        return cmd;
    }

    /// <summary>Whether an agent reporting <paramref name="agentVersion"/> verifies version 2 signatures.</summary>
    public static bool UsesV2(string? agentVersion) =>
        Version.TryParse(agentVersion, out var v) && v >= SigV2MinAgent;

    public void Dispose() => _privateKey.Dispose();
}
