using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RemoteServer.Configuration;
using RemoteServer.Security;

namespace RemoteAppClient.Tests.Server;

/// <summary>What the database holds for you (VNC passwords, notes, TOTP secrets) is useless without the key file.</summary>
public class SecretProtectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rac-tests-" + Guid.NewGuid().ToString("N"));
    public SecretProtectorTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private SecretProtector With(byte[] key)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".key");
        File.WriteAllBytes(path, key);
        return new SecretProtector(Options.Create(new ServerOptions { SecretKeyPath = path }), NullLogger<SecretProtector>.Instance);
    }

    [Fact]
    public void Refuses_to_start_without_a_32_byte_key()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new SecretProtector(Options.Create(new ServerOptions { SecretKeyPath = Path.Combine(_root, "missing.key") }), NullLogger<SecretProtector>.Instance));
        Assert.Throws<InvalidOperationException>(() => With(RandomNumberGenerator.GetBytes(16)));
    }

    [Fact]
    public void Round_trips_with_a_fresh_nonce_every_time()
    {
        var p = With(RandomNumberGenerator.GetBytes(32));
        var a = p.Protect("vnc-pass-1");
        var b = p.Protect("vnc-pass-1");
        Assert.NotEqual(a, b);
        Assert.Equal("vnc-pass-1", p.TryUnprotect(a));
        Assert.Equal("vnc-pass-1", p.TryUnprotect(b));
        Assert.Equal("", p.TryUnprotect(p.Protect("")));
    }

    [Fact]
    public void Anything_but_an_intact_blob_under_this_key_reads_as_null()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var p = With(key);
        var blob = p.Protect("note");

        Assert.Null(p.TryUnprotect(null));
        Assert.Null(p.TryUnprotect(""));
        Assert.Null(p.TryUnprotect("not base64!"));
        Assert.Null(p.TryUnprotect(Convert.ToBase64String(new byte[10])));

        var bytes = Convert.FromBase64String(blob);
        bytes[^1] ^= 0x01;                                   // flip a ciphertext bit: the tag no longer matches
        Assert.Null(p.TryUnprotect(Convert.ToBase64String(bytes)));

        var other = With(RandomNumberGenerator.GetBytes(32));
        Assert.Null(other.TryUnprotect(blob));               // a dump plus the wrong key is still nothing
    }
}
