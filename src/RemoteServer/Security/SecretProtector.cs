using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RemoteServer.Configuration;

namespace RemoteServer.Security;

/// <summary>
/// DB-beli titkok nyugalmi titkosítása (AES-256-GCM). A 32 bájtos kulcs egy külön,
/// service-user által olvasható fájlban van (/etc/remoteserver/secret.key), így egy
/// puszta DB-dump nem fedi fel a titkokat. Formátum: base64(nonce[12] | tag[16] | ct).
/// </summary>
public sealed class SecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public SecretProtector(IOptions<ServerOptions> options, ILogger<SecretProtector> logger)
    {
        var path = options.Value.SecretKeyPath;
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"A titkosító kulcs hiányzik ('{path}'). Generáld provisioning-kor (deploy-server.sh).");

        _key = File.ReadAllBytes(path);
        if (_key.Length != 32)
            throw new InvalidOperationException($"A titkosító kulcs nem 32 bájt ({_key.Length}).");

        logger.LogInformation("Titok-titkosító kulcs betöltve.");
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length];
        var tag = new byte[TagSize];

        using var gcm = new AesGcm(_key, TagSize);
        gcm.Encrypt(nonce, pt, ct, tag);

        var blob = new byte[NonceSize + TagSize + ct.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        ct.CopyTo(blob, NonceSize + TagSize);
        return Convert.ToBase64String(blob);
    }

    /// <summary>Visszafejt; null ha üres vagy nem értelmezhető (pl. régi/sérült érték).</summary>
    public string? TryUnprotect(string? blobBase64)
    {
        if (string.IsNullOrEmpty(blobBase64)) return null;
        try
        {
            var blob = Convert.FromBase64String(blobBase64);
            if (blob.Length < NonceSize + TagSize) return null;

            var nonce = blob.AsSpan(0, NonceSize);
            var tag = blob.AsSpan(NonceSize, TagSize);
            var ct = blob.AsSpan(NonceSize + TagSize);
            var pt = new byte[ct.Length];

            using var gcm = new AesGcm(_key, TagSize);
            gcm.Decrypt(nonce, ct, tag, pt);
            return Encoding.UTF8.GetString(pt);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
