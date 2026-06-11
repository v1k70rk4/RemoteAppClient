using System.Security.Cryptography;
using System.Text;

namespace RemoteAgent.Vnc;

/// <summary>
/// A VNC-jelszó kódolása a TightVNC registry "Password" mezőjéhez. Ez a klasszikus
/// VNC formátum: a (max 8 karakteres) jelszó DES-ECB titkosítása a jól ismert fix
/// kulccsal, amelynek minden bájtja bit-tükrözött. Ez INTEROP, nem biztonsági cél —
/// a tényleges védelmet az SSH-tunnel + a loopback-only adja.
/// </summary>
public static class VncPassword
{
    // A klasszikus VNC fix kulcs.
    private static readonly byte[] FixedKey = [23, 82, 107, 6, 35, 78, 88, 7];

    public static byte[] Encrypt(string password)
    {
        var key = (byte[])FixedKey.Clone();
        for (int i = 0; i < key.Length; i++) key[i] = MirrorBits(key[i]);

        var data = new byte[8];
        var pw = Encoding.ASCII.GetBytes(password);
        Array.Copy(pw, data, Math.Min(8, pw.Length));

#pragma warning disable CA5351 // A DES itt a VNC-formátum kötelező része (interop), nem védelmi mechanizmus.
        using var des = DES.Create();
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
