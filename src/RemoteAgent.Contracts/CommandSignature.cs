using System.Security.Cryptography;
using System.Text;

namespace RemoteAgent.Commands;

/// <summary>
/// A parancs-aláírás EGYETLEN igazsága. A szerver <see cref="Sign"/>-ol, a kliens
/// <see cref="Verify"/>-ol — mindkettő ugyanezt a <see cref="Canonicalize"/> formát
/// használja, így a két oldal definíció szerint nem csúszhat szét.
///
/// Algoritmus: ECDSA P-256 / SHA-256. (A .NET BCL nem ad natív Ed25519-et, és ez
/// AOT-barát is.) Az aláírás IEEE-P1363 (r||s) formátumú, Base64-ben a sig mezőben.
/// </summary>
public static class CommandSignature
{
    /// <summary>
    /// Determinisztikus, mezősorrend-független szöveg, amit aláírunk/ellenőrzünk.
    /// Az aláírás NEM tartalmazza önmagát.
    /// </summary>
    public static string Canonicalize(AgentCommand cmd) =>
        $"{cmd.Type}|{cmd.Nonce}|{cmd.IssuedAt}|{cmd.Data?.RemotePort ?? 0}" +
        $"|{cmd.Data?.UpdateVersion}|{cmd.Data?.UpdateUrl}|{cmd.Data?.UpdateSha256}|{cmd.Data?.UpdateTarget}" +
        $"|{cmd.Data?.ConsentRequired ?? false}|{cmd.Data?.UnattendedAllowed ?? true}";

    /// <summary>Aláírja a parancsot a szerver privát kulcsával, és beállítja a Signature mezőt.</summary>
    public static void Sign(AgentCommand cmd, ECDsa privateKey)
    {
        byte[] payload = Encoding.UTF8.GetBytes(Canonicalize(cmd));
        byte[] sig = privateKey.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        cmd.Signature = Convert.ToBase64String(sig);
    }

    /// <summary>
    /// Ellenőrzi a parancs aláírását a szerver publikus kulcsával.
    /// CSAK az aláírást nézi — a nonce/timestamp replay-ellenőrzés a hívó dolga.
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
