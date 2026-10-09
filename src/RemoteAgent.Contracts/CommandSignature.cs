using System.Security.Cryptography;
using System.Text;

namespace RemoteAgent.Commands;

/// <summary>
/// Single source of truth for command signatures. The server calls <see cref="Sign"/>
/// and the client calls <see cref="Verify"/>; both use the same canonical form, so the two sides cannot drift.
///
/// Two canonical forms exist. Version 1 (<see cref="Canonicalize(AgentCommand)"/>) is the original
/// '|'-joined text: it covers the fields that existed when each command type was added and nothing else,
/// and agents before 2.2.7 verify only that. Version 2 (<see cref="Canonicalize(AgentCommand, string)"/>)
/// covers every field, length-prefixed so no value can masquerade as a separator, and binds the command to
/// the device it was issued for, so a command captured for one device cannot be fed to another. The server
/// signs version 2 for agents that understand it (<see cref="AgentCommand.SigVersion"/> = 2) and version 1
/// for the rest; an agent verifies whichever version the command names.
///
/// Algorithm: ECDSA P-256 / SHA-256. The .NET BCL does not provide native Ed25519 here,
/// and this is AOT-friendly. The signature is IEEE-P1363 (r||s), Base64-encoded in sig.
/// </summary>
public static class CommandSignature
{
    /// <summary>The newest canonical form this build can produce and check.</summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// Version 1: deterministic, field-order-independent text that is signed and verified.
    /// The signature does not include itself. Frozen: agents in the field verify exactly this.
    /// </summary>
    public static string Canonicalize(AgentCommand cmd)
    {
        var s = $"{cmd.Type}|{cmd.Nonce}|{cmd.IssuedAt}|{cmd.Data?.RemotePort ?? 0}" +
                $"|{cmd.Data?.UpdateVersion}|{cmd.Data?.UpdateUrl}|{cmd.Data?.UpdateSha256}|{cmd.Data?.UpdateTarget}" +
                $"|{cmd.Data?.ConsentRequired ?? false}|{cmd.Data?.UnattendedAllowed ?? true}";

        // Message fields are appended ONLY for message commands, so existing commands' canonical
        // form (and signatures) stay unchanged — older agents keep verifying them.
        if (cmd.Type == CommandTypes.Message)
            s += $"|{cmd.Data?.MessageKind}|{cmd.Data?.MessageFrom}|{cmd.Data?.MessageText}";

        // Likewise, power fields are appended only for power commands.
        if (cmd.Type == CommandTypes.Power)
            s += $"|{cmd.Data?.PowerAction}";

        // And the diag hours only for diag commands.
        if (cmd.Type == CommandTypes.Diag)
            s += $"|{cmd.Data?.DiagHours ?? 0}";
        return s;
    }

    /// <summary>
    /// Version 2: every field of the command plus the device it is for, each as "length:value;" (a missing
    /// value is "-;", distinct from an empty one), so the form is unambiguous whatever the values contain.
    /// A field added later goes at the end under a new version number; this one is frozen too.
    /// </summary>
    public static string Canonicalize(AgentCommand cmd, string deviceId)
    {
        var d = cmd.Data;
        var sb = new StringBuilder("v2;");
        Add(sb, deviceId);
        Add(sb, cmd.Type);
        Add(sb, cmd.Nonce);
        Add(sb, cmd.IssuedAt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(sb, (d?.RemotePort ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(sb, d?.UpdateVersion);
        Add(sb, d?.UpdateUrl);
        Add(sb, d?.UpdateSha256);
        Add(sb, d?.UpdateTarget);
        Add(sb, Flag(d?.ConsentRequired));
        Add(sb, Flag(d?.UnattendedAllowed));
        Add(sb, d?.MessageKind);
        Add(sb, d?.MessageFrom);
        Add(sb, d?.MessageText);
        Add(sb, d?.PowerAction);
        Add(sb, d?.DiagHours?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(sb, (d?.FileRemotePort ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(sb, d?.FileToken);
        Add(sb, d?.TunnelPurpose);
        return sb.ToString();

        static void Add(StringBuilder sb, string? value)
        {
            if (value is null) { sb.Append("-;"); return; }
            sb.Append(Encoding.UTF8.GetByteCount(value)).Append(':').Append(value).Append(';');
        }
        static string? Flag(bool? b) => b is null ? null : b.Value ? "1" : "0";
    }

    private static byte[] Payload(AgentCommand cmd, string? deviceId) =>
        Encoding.UTF8.GetBytes(cmd.SigVersion >= 2 ? Canonicalize(cmd, deviceId ?? "") : Canonicalize(cmd));

    /// <summary>Signs the command with the server private key and sets the Signature field. With
    /// <paramref name="deviceId"/> the version 2 form is used and marked on the command; without it, version 1.</summary>
    public static void Sign(AgentCommand cmd, ECDsa privateKey, string? deviceId = null)
    {
        cmd.SigVersion = deviceId is null ? 0 : CurrentVersion;
        byte[] sig = privateKey.SignData(Payload(cmd, deviceId), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        cmd.Signature = Convert.ToBase64String(sig);
    }

    /// <summary>
    /// Verifies the command signature with the server public key, in the version the command names;
    /// <paramref name="deviceId"/> is this device's own id, which a version 2 signature must have been made for.
    /// A version this build does not know is refused. This checks only the signature; nonce/timestamp replay
    /// checks belong to the caller.
    /// </summary>
    public static bool Verify(AgentCommand cmd, ECDsa publicKey, string? deviceId = null)
    {
        if (string.IsNullOrEmpty(cmd.Signature))
            return false;
        if (cmd.SigVersion > CurrentVersion)
            return false;
        if (cmd.SigVersion >= 2 && string.IsNullOrEmpty(deviceId))
            return false;

        byte[] sig;
        try
        {
            sig = Convert.FromBase64String(cmd.Signature);
        }
        catch (FormatException)
        {
            return false;
        }

        return publicKey.VerifyData(Payload(cmd, deviceId), sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
