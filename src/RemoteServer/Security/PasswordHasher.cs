using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace RemoteServer.Security;

/// <summary>
/// Argon2id password hash. Hashes are self-describing: they include parameters plus salt,
/// so parameters can be raised later without invalidating existing hashes.
/// Format: <c>$argon2id$m=&lt;kib&gt;,t=&lt;iter&gt;,p=&lt;par&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;</c>
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

    // Each check takes 64 MiB and a few hundred milliseconds of CPU, and the sign-in endpoints answer anyone. Only
    // this many run at once; the rest wait their turn, so a burst of attempts queues instead of exhausting memory.
    private static readonly SemaphoreSlim Concurrent = new(Math.Clamp(Environment.ProcessorCount, 2, 4));
    private static readonly TimeSpan QueueTimeout = TimeSpan.FromSeconds(20);

    // A hash of nothing in particular, checked against when the user does not exist, so an unknown name costs the
    // same time as a wrong password and the answer's timing does not tell which it was.
    private static readonly Lazy<string> DummyHash = new(() => Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))));

    /// <summary>
    /// Checks a password against a stored hash, or against a dummy when <paramref name="stored"/> is null (unknown
    /// user - always false, same cost). Null when too many checks are queued already: the caller answers "busy".
    /// </summary>
    public static async Task<bool?> VerifyAsync(string password, string? stored, CancellationToken ct)
    {
        if (!await Concurrent.WaitAsync(QueueTimeout, ct)) return null;
        try
        {
            return await Task.Run(() =>
            {
                bool ok = Verify(password, stored ?? DummyHash.Value);
                return stored is not null && ok;
            }, ct);
        }
        finally { Concurrent.Release(); }
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
