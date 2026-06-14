using System.Security.Cryptography;
using System.Text;

namespace RemoteAgent.Commands;

/// <summary>
/// Single source of truth for command signatures. The server calls <see cref="Sign"/>
/// and the client calls <see cref="Verify"/>; both use the same <see cref="Canonicalize"/>
/// form, so the two sides cannot drift.
///
/// Algorithm: ECDSA P-256 / SHA-256. The .NET BCL does not provide native Ed25519 here,
/// and this is AOT-friendly. The signature is IEEE-P1363 (r||s), Base64-encoded in sig.
/// </summary>
public static class CommandSignature
{
    /// <summary>
    /// Deterministic, field-order-independent text that is signed and verified.
    /// The signature does not include itself.
    /// </summary>
    public static string Canonicalize(AgentCommand cmd) =>
        $"{cmd.Type}|{cmd.Nonce}|{cmd.IssuedAt}|{cmd.Data?.RemotePort ?? 0}" +
        $"|{cmd.Data?.UpdateVersion}|{cmd.Data?.UpdateUrl}|{cmd.Data?.UpdateSha256}|{cmd.Data?.UpdateTarget}" +
        $"|{cmd.Data?.ConsentRequired ?? false}|{cmd.Data?.UnattendedAllowed ?? true}";

    /// <summary>Signs the command with the server private key and sets the Signature field.</summary>
    public static void Sign(AgentCommand cmd, ECDsa privateKey)
    {
        byte[] payload = Encoding.UTF8.GetBytes(Canonicalize(cmd));
        byte[] sig = privateKey.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        cmd.Signature = Convert.ToBase64String(sig);
    }

    /// <summary>
    /// Verifies the command signature with the server public key.
    /// This checks only the signature; nonce/timestamp replay checks belong to the caller.
    /// </summary>
    public static bool Verify(AgentCommand cmd, ECDsa publicKey)
    {
        if (string.IsNullOrEmpty(cmd.Signature))
            return false;

        byte[] payload = Encoding.UTF8.GetBytes(Canonicalize(cmd));
        byte[] sig;
        try
        {
            sig = Convert.FromBase64String(cmd.Signature);
        }
        catch (FormatException)
        {
            return false;
        }

        return publicKey.VerifyData(payload, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
