using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RemoteAgent.Security;

/// <summary>
/// The OpenSSH wire encodings the in-process ssh-agent needs for an ECDSA P-256 key: the public key blob and
/// line, the signature blob, the agent protocol's framing, and the "SSHSIG" proof-of-possession format that
/// <c>ssh-keygen -Y verify</c> checks. Pure bytes, no CNG, so it is testable; the key itself stays in
/// <see cref="SshKeyStore"/>.
/// </summary>
public static class SshKeyEncoding
{
    public const string KeyType = "ecdsa-sha2-nistp256";
    public const string Curve = "nistp256";

    // ssh-agent protocol (draft-miller-ssh-agent): what a client sends and what we answer.
    public const byte AgentFailure = 5;
    public const byte AgentSuccess = 6;
    public const byte AgentcRequestIdentities = 11;
    public const byte AgentIdentitiesAnswer = 12;
    public const byte AgentcSignRequest = 13;
    public const byte AgentSignResponse = 14;

    /// <summary>The public key blob: string type, string curve, string 0x04 || X || Y (each coordinate 32 bytes).</summary>
    public static byte[] PublicKeyBlob(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        var point = new byte[65];
        point[0] = 4;
        Fixed(x, point.AsSpan(1, 32));
        Fixed(y, point.AsSpan(33, 32));
        var w = new Writer();
        w.String(KeyType); w.String(Curve); w.Bytes(point);
        return w.ToArray();
    }

    /// <summary>The one-line public form, "ecdsa-sha2-nistp256 base64 comment".</summary>
    public static string PublicKeyLine(byte[] blob, string comment) => $"{KeyType} {Convert.ToBase64String(blob)} {comment}";

    /// <summary>The ssh signature blob for a raw r||s (IEEE P1363, 64 bytes): string type, string(mpint r, mpint s).</summary>
    public static byte[] SignatureBlob(ReadOnlySpan<byte> rs)
    {
        if (rs.Length != 64) throw new ArgumentException("A P-256 signature is 64 bytes of r||s.", nameof(rs));
        var inner = new Writer();
        inner.Mpint(rs[..32]); inner.Mpint(rs[32..]);
        var w = new Writer();
        w.String(KeyType); w.Bytes(inner.ToArray());
        return w.ToArray();
    }

    /// <summary>
    /// An armored OpenSSH signature ("SSHSIG", version 1) over <paramref name="message"/> in <paramref name="ns"/>,
    /// as ssh-keygen -Y sign makes and ssh-keygen -Y verify checks. The signer gets the exact bytes to sign
    /// (SHA-256 over them, ECDSA) and returns r||s.
    /// </summary>
    public static string SshSig(byte[] publicKeyBlob, string ns, ReadOnlySpan<byte> message, Func<byte[], byte[]> signRs)
    {
        var toSign = new Writer();
        toSign.Raw("SSHSIG"u8);
        toSign.String(ns); toSign.String(""); toSign.String("sha256"); toSign.Bytes(SHA256.HashData(message));
        var sig = SignatureBlob(signRs(toSign.ToArray()));

        var w = new Writer();
        w.Raw("SSHSIG"u8);
        w.UInt32(1);
        w.Bytes(publicKeyBlob); w.String(ns); w.String(""); w.String("sha256"); w.Bytes(sig);
        var b64 = Convert.ToBase64String(w.ToArray());
        var sb = new StringBuilder("-----BEGIN SSH SIGNATURE-----\n");
        for (var i = 0; i < b64.Length; i += 70) sb.Append(b64, i, Math.Min(70, b64.Length - i)).Append('\n');
        return sb.Append("-----END SSH SIGNATURE-----\n").ToString();
    }

    // ---- agent protocol ------------------------------------------------------------------------------

    /// <summary>The identities answer for one key: uint32 count, string blob, string comment.</summary>
    public static byte[] IdentitiesAnswer(byte[] keyBlob, string comment) => IdentitiesAnswer([(keyBlob, comment)]);

    /// <summary>The identities answer for several keys.</summary>
    public static byte[] IdentitiesAnswer(IEnumerable<(byte[] Blob, string Comment)> keys)
    {
        var list = keys.ToList();
        var w = new Writer();
        w.Byte(AgentIdentitiesAnswer); w.UInt32((uint)list.Count);
        foreach (var (blob, comment) in list) { w.Bytes(blob); w.String(comment); }
        return w.ToArray();
    }

    /// <summary>The plain public key blob embedded in an ECDSA certificate blob
    /// ("ecdsa-sha2-nistp256-cert-v01@openssh.com": type, nonce, curve, point, ...); null for anything else.
    /// ssh signs certificate authentication naming the certificate, and the agent holds the plain key.</summary>
    public static byte[]? PlainKeyOfCertificate(ReadOnlySpan<byte> certBlob)
    {
        try
        {
            var r = new Reader(certBlob);
            var type = Encoding.ASCII.GetString(r.String());
            if (type != KeyType + "-cert-v01@openssh.com") return null;
            r.String(); // nonce
            var curve = r.String();
            var point = r.String();
            var w = new Writer();
            w.String(KeyType); w.Bytes(curve); w.Bytes(point);
            return w.ToArray();
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    public static byte[] SignResponse(byte[] signatureBlob)
    {
        var w = new Writer();
        w.Byte(AgentSignResponse); w.Bytes(signatureBlob);
        return w.ToArray();
    }

    public static byte[] Failure() => [AgentFailure];

    /// <summary>A sign request's fields after the message byte: string key blob, string data, uint32 flags.</summary>
    public static (byte[] KeyBlob, byte[] Data, uint Flags)? ParseSignRequest(ReadOnlySpan<byte> body)
    {
        try
        {
            var r = new Reader(body);
            var key = r.String().ToArray();
            var data = r.String().ToArray();
            var flags = r.UInt32();
            return (key, data, flags);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Frames a message for the pipe: uint32 length, then the message.</summary>
    public static byte[] Frame(byte[] message)
    {
        var o = new byte[4 + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(o, (uint)message.Length);
        message.CopyTo(o, 4);
        return o;
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static void Fixed(ReadOnlySpan<byte> v, Span<byte> dest)
    {
        v = v.TrimStart((byte)0);
        if (v.Length > dest.Length) throw new ArgumentException("Coordinate longer than the curve's field.");
        dest.Clear();
        v.CopyTo(dest[(dest.Length - v.Length)..]);
    }

    public sealed class Writer
    {
        private readonly MemoryStream _m = new();
        public void Byte(byte b) => _m.WriteByte(b);
        public void Raw(ReadOnlySpan<byte> b) => _m.Write(b);
        public void UInt32(uint v) { Span<byte> t = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(t, v); _m.Write(t); }
        public void Bytes(ReadOnlySpan<byte> b) { UInt32((uint)b.Length); _m.Write(b); }
        public void String(string s) => Bytes(Encoding.UTF8.GetBytes(s));
        /// <summary>A positive multiple-precision integer: leading zeros stripped, a zero byte prepended when
        /// the high bit is set, so the number never reads as negative.</summary>
        public void Mpint(ReadOnlySpan<byte> unsigned)
        {
            unsigned = unsigned.TrimStart((byte)0);
            if (unsigned.Length == 0) { UInt32(0); return; }
            if ((unsigned[0] & 0x80) != 0) { UInt32((uint)unsigned.Length + 1); _m.WriteByte(0); _m.Write(unsigned); }
            else Bytes(unsigned);
        }
        public byte[] ToArray() => _m.ToArray();
    }

    public ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos = 0;
        public ReadOnlySpan<byte> String()
        {
            var len = checked((int)UInt32());
            if (len > _data.Length - _pos) throw new ArgumentOutOfRangeException(nameof(len));
            var s = _data.Slice(_pos, len); _pos += len; return s;
        }
        public uint UInt32()
        {
            if (_data.Length - _pos < 4) throw new ArgumentOutOfRangeException(nameof(_pos));
            var v = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(_pos, 4)); _pos += 4; return v;
        }
    }
}
