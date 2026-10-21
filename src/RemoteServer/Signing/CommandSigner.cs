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
    public AgentCommand Create(string type, CommandData? data = null, string? deviceId = null, int version = CommandSignature.CurrentVersion)
    {
        var cmd = new AgentCommand
        {
            Type = type,
            Nonce = Guid.NewGuid().ToString("N"),
            IssuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Data = data,
        };
        CommandSignature.Sign(cmd, _privateKey, deviceId, deviceId is null ? CommandSignature.CurrentVersion : version);   // no device: the unbound form, whatever the version
        return cmd;
    }

    /// <summary>Agents from here on check the version 3 form, which covers the session's VNC password.</summary>
    public static readonly Version SigV3MinAgent = RemoteAgent.Admin.DeviceInfo.SessionPasswordMinAgent;

    /// <summary>The signature version an agent reporting <paramref name="agentVersion"/> checks: 3 from
    /// <see cref="SigV3MinAgent"/>, 2 from <see cref="SigV2MinAgent"/>, 1 (the unbound form) below that. A
    /// device whose version is not known yet is a fresh install and gets the newest.</summary>
    public static int VersionFor(string? agentVersion)
    {
        if (!Version.TryParse(agentVersion, out var v)) return CommandSignature.CurrentVersion;
        if (v >= SigV3MinAgent) return 3;
        if (v >= SigV2MinAgent) return 2;
        return 1;
    }

    /// <summary>Whether an agent reporting <paramref name="agentVersion"/> verifies version 2 signatures.</summary>
    /// <summary>Whether the device gets the device-bound signature: every agent from <see cref="SigV2MinAgent"/>,
    /// and a device whose version is not known yet. A device without a reported version is a fresh install (a
    /// re-imaged machine enrolling anew, say), and a fresh install always runs an agent that checks the binding;
    /// the unbound form would be refused by it until its first report.</summary>
    public static bool UsesV2(string? agentVersion) =>
        !Version.TryParse(agentVersion, out var v) || v >= SigV2MinAgent;

    public void Dispose() => _privateKey.Dispose();
}
