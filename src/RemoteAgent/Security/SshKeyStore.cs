using System.Security.Cryptography;

namespace RemoteAgent.Security;

/// <summary>
/// The tunnel key as a named, machine-scoped, non-exportable CNG key - in the TPM when it is usable, otherwise in
/// the software key storage provider - exactly like the device key, under its own name prefix. OpenSSH never
/// sees the private key: the in-process ssh-agent signs with it, and only the public key line and the
/// certificate are written next to the enrollment. P-256, because that is what TPMs and OpenSSH agree on
/// (ecdsa-sha2-nistp256); ed25519 stays the format of the file keys from older enrollments.
/// </summary>
public static class SshKeyStore
{
    public const string PipeName = "RemoteAgent.ssh-agent";
    public static string PipePath => @"\\.\pipe\" + PipeName;

    public const string PublicKeyFile = "id_ecdsa.pub";
    public const string CertificateFile = "id_ecdsa-cert.pub";

    public sealed record NewKey(ECDsa Key, string Provider, string KeyName, byte[] PublicKeyBlob) : IDisposable
    {
        public string PublicKeyLine => SshKeyEncoding.PublicKeyLine(PublicKeyBlob, "remoteagent");
        public void Dispose() => Key.Dispose();
    }

    /// <summary>Creates a fresh tunnel key in CNG. Throws CryptographicException when even the software provider
    /// refuses; the caller then falls back to an OpenSSH key file.</summary>
    public static NewKey Create(bool preferTpm)
    {
        var k = DeviceKeyStore.CreateNamed("RemoteAgent-Ssh-", preferTpm);
        return new NewKey(k.Key, k.Provider, k.KeyName, PublicKeyBlob(k.Key));
    }

    public static ECDsa? Open(string? keyName) => DeviceKeyStore.Open(keyName);
    public static void Delete(string? keyName) => DeviceKeyStore.DeleteKey(keyName);
    public static bool Exists(string? keyName) => DeviceKeyStore.KeyExists(keyName);

    /// <summary>The OpenSSH public key blob of a P-256 key.</summary>
    public static byte[] PublicKeyBlob(ECDsa key)
    {
        var p = key.ExportParameters(false);
        return SshKeyEncoding.PublicKeyBlob(p.Q.X!, p.Q.Y!);
    }

    /// <summary>ECDSA over SHA-256 of the data, as r||s (64 bytes) - what the ssh signature blob is built from.</summary>
    public static byte[] SignRs(ECDsa key, ReadOnlySpan<byte> data) =>
        key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
}
