using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RemoteServer.Configuration;
using RemoteServer.Signing;

namespace RemoteAppClient.Tests.Server;

/// <summary>
/// The bastion's revocation list for deleted devices, against the real ssh-keygen. Linux only, like the server:
/// on any other system the list does nothing and these tests have nothing to check (CI runs them on Ubuntu).
/// </summary>
public class SshRevocationListTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rac-krl-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task A_deleted_devices_key_and_its_certificate_are_revoked_and_no_other()
    {
        if (!OperatingSystem.IsLinux()) return;
        Directory.CreateDirectory(_root);
        foreach (var k in new[] { "ca", "dev1", "dev2" }) Run("-q", "-t", "ed25519", "-N", "", "-f", Path.Combine(_root, k));
        Run("-q", "-s", Path.Combine(_root, "ca"), "-I", "dev1", "-n", "agent", "-V", "+825d", Path.Combine(_root, "dev1.pub"));
        Run("-q", "-s", Path.Combine(_root, "ca"), "-I", "dev2", "-n", "agent", "-V", "+825d", Path.Combine(_root, "dev2.pub"));

        var krl = Path.Combine(_root, "ssh", "revoked_keys.krl");
        var list = new SshRevocationList(
            Options.Create(new ServerOptions { Bastion = new BastionOptions { RevokedKeysPath = krl } }),
            NullLogger<SshRevocationList>.Instance);

        // At start the list exists, empty: sshd may be pointed at it without locking anybody out.
        await list.StartAsync(CancellationToken.None);
        Assert.True(File.Exists(krl));
        Assert.Equal(0, Query(krl, "dev1-cert.pub"));

        await list.RevokeAsync(File.ReadAllText(Path.Combine(_root, "dev1.pub")), "dev1-id", "HOST\n1", CancellationToken.None);
        Assert.NotEqual(0, Query(krl, "dev1.pub"));        // the key
        Assert.NotEqual(0, Query(krl, "dev1-cert.pub"));   // and every certificate issued for it
        Assert.Equal(0, Query(krl, "dev2-cert.pub"));      // another device is untouched
        Assert.False(File.Exists(krl + ".tmp"));

        // A hostname cannot break the list's lines (the newline above stayed out of it), and a rebuild at the
        // next start keeps every key.
        Assert.All(File.ReadAllLines(Path.ChangeExtension(krl, ".txt")), l => Assert.True(l.Length == 0 || l.StartsWith('#') || l.StartsWith("ssh-")));
        File.Delete(krl);
        await list.StartAsync(CancellationToken.None);
        Assert.NotEqual(0, Query(krl, "dev1-cert.pub"));
    }

    private int Query(string krl, string file)
    {
        var psi = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-Q", "-f", krl, Path.Combine(_root, file) }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    private static void Run(params string[] args)
    {
        var psi = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}
