using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace RemoteServer.Security;

/// <summary>
/// Argon2id jelszó-hash. A hash önleíró: tartalmazza a paramétereket + sót, így a
/// paraméterek később emelhetők a meglévő hashek érvénytelenítése nélkül.
/// Formátum: <c>$argon2id$m=&lt;kib&gt;,t=&lt;iter&gt;,p=&lt;par&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;</c>
/// </summary>
public static class PasswordHasher
{
    private const int MemoryKib = 65536;   // 64 MB
    private const int Iterations = 3;
    private const int Parallelism = 4;
    private const int SaltLen = 16;
    private const int HashLen = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var hash = Derive(password, salt, MemoryKib, Iterations, Parallelism, HashLen);
        return $"$argon2id$m={MemoryKib},t={Iterations},p={Parallelism}$" +
               $"{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        try
        {
            // $argon2id$m=..,t=..,p=..$salt$hash
            var parts = stored.Split('$', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4 || parts[0] != "argon2id") return false;

            var p = parts[1].Split(',');
            int m = int.Parse(p[0][2..]);
            int t = int.Parse(p[1][2..]);
            int par = int.Parse(p[2][2..]);
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);

            var actual = Derive(password, salt, m, t, par, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    private static byte[] Derive(string password, byte[] salt, int memKib, int iter, int par, int len)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memKib,
            Iterations = iter,
            DegreeOfParallelism = par,
        };
        return argon2.GetBytes(len);
    }
}
