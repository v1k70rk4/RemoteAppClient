using System.Security.Cryptography;
using System.Text;

namespace RemoteAgent.Vnc;

/// <summary>
/// Encodes the VNC password in the classic fixed-key DES format used by VNC.
/// TightVNC stores it in the registry "Password" value, and the viewer .vnc file uses it too.
/// Shared between provisioning and the admin client auto-connect flow.
/// This is interoperability, not security; protection comes from the SSH tunnel and loopback-only binding.
/// </summary>
public static class VncPassword
{
    private static readonly byte[] FixedKey = [23, 82, 107, 6, 35, 78, 88, 7];

    public static byte[] Encrypt(string password)
    {
        var key = (byte[])FixedKey.Clone();
        for (int i = 0; i < key.Length; i++) key[i] = MirrorBits(key[i]);

        var data = new byte[8];
        var pw = Encoding.ASCII.GetBytes(password);
        Array.Copy(pw, data, Math.Min(8, pw.Length));

        // DES is mandated by the VNC password format (interop with TightVNC), not a protection mechanism;
        // real protection is the SSH tunnel + loopback-only binding (see class summary). Hence the suppressions.
#pragma warning disable CA5351
        using var des = DES.Create(); // nosemgrep: csharp.dotnet.security.use_deprecated_cipher_algorithm.use_deprecated_cipher_algorithm
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;
        using var enc = des.CreateEncryptor(key, null);
        return enc.TransformFinalBlock(data, 0, 8);
#pragma warning restore CA5351
    }

    private static byte MirrorBits(byte b)
    {
        byte r = 0;
        for (int i = 0; i < 8; i++)
        {
            r = (byte)((r << 1) | (b & 1));
            b >>= 1;
        }
        return r;
    }
}
