using System.Buffers.Binary;
using System.Text;

namespace RemoteAgent.Security;

/// <summary>
/// Reads the validity of an OpenSSH certificate (<c>id_ed25519-cert.pub</c>) without ssh-keygen, and decides
/// when the agent asks the server for a new one. Pure, so it is testable.
///
/// An agent's SSH certificate is short-lived (days): the private key stays a file, but a copy of it is worth
/// nothing once its certificate ends, and a new certificate is issued only to the device that holds the TPM-bound
/// client certificate (<c>POST /api/ssh-cert</c> over mTLS). The bastion's revocation list keeps covering the
/// cases that cannot wait for an expiry.
/// </summary>
public static class SshCertificate
{
    /// <summary>What the wire format says about a certificate. Times are UTC.</summary>
    public sealed record Validity(string Type, ulong Serial, string KeyId, DateTimeOffset ValidAfter, DateTimeOffset ValidBefore);

    /// <summary>How long a new certificate is meant to live; longer ones (the 825-day certificates from enrollment)
    /// are replaced by a key rotation, so copies of the old key stop working through the revocation list.</summary>
    public static readonly TimeSpan MaxWanted = TimeSpan.FromDays(30);

    /// <summary>A certificate with less than this left is renewed. Half of the server's three-day validity, so a
    /// check every few hours can never miss the window.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromHours(36);

    /// <summary>"renew" when the certificate is missing, unreadable or near its end; "rotate" when it is one of the
    /// long-lived ones; null when nothing is due.</summary>
    public static string? Due(Validity? cert, DateTimeOffset now)
    {
        if (cert is null) return "renew";
        if (cert.ValidBefore - now < RenewBefore) return "renew";
        if (cert.ValidBefore - cert.ValidAfter > MaxWanted) return "rotate";
        return null;
    }

    /// <summary>"type base64" of a public key or certificate line, without the comment; what a proof of
    /// possession is made over, so both sides sign and verify the very same bytes.</summary>
    public static string? KeyBody(string? line)
    {
        var parts = (line ?? "").Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[0] + " " + parts[1] : null;
    }

    /// <summary>Parses the one-line public form ("type base64 comment"). Null when it is not a certificate this
    /// understands. OpenSSH's format: type, nonce, the public key's own fields (one for ed25519, two for RSA
    /// and ECDSA), serial, cert type, key id, principals, valid_after, valid_before, ...</summary>
    public static Validity? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        byte[] blob;
        try { blob = Convert.FromBase64String(parts[1]); }
        catch (FormatException) { return null; }

        try
        {
            var r = new Reader(blob);
            var type = Encoding.ASCII.GetString(r.String());
            if (!string.Equals(type, parts[0], StringComparison.Ordinal) || !type.EndsWith("-cert-v01@openssh.com", StringComparison.Ordinal))
                return null;
            r.String(); // nonce
            var keyFields = type.StartsWith("ssh-ed25519", StringComparison.Ordinal) ? 1 : 2; // ed25519: pk; rsa: e, n; ecdsa: curve, point
            for (var i = 0; i < keyFields; i++) r.String();
            var serial = r.UInt64();
            r.UInt32(); // cert type (user/host)
            var keyId = Encoding.UTF8.GetString(r.String());
            r.String(); // principals
            var after = r.UInt64();
            var before = r.UInt64();
            return new Validity(type, serial, keyId, FromUnix(after), FromUnix(before));
        }
        catch (ArgumentOutOfRangeException) { return null; } // truncated blob
    }

    private static DateTimeOffset FromUnix(ulong seconds) =>
        seconds >= (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds() ? DateTimeOffset.MaxValue : DateTimeOffset.FromUnixTimeSeconds((long)seconds);

    private ref struct Reader(byte[] data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos = 0;

        public ReadOnlySpan<byte> String()
        {
            var len = checked((int)UInt32());
            if (len > _data.Length - _pos) throw new ArgumentOutOfRangeException(nameof(len));
            var s = _data.Slice(_pos, len);
            _pos += len;
            return s;
        }

        public uint UInt32()
        {
            if (_data.Length - _pos < 4) throw new ArgumentOutOfRangeException(nameof(_pos));
            var v = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(_pos, 4));
            _pos += 4;
            return v;
        }

        public ulong UInt64()
        {
            if (_data.Length - _pos < 8) throw new ArgumentOutOfRangeException(nameof(_pos));
            var v = BinaryPrimitives.ReadUInt64BigEndian(_data.Slice(_pos, 8));
            _pos += 8;
            return v;
        }
    }
}
