using System.Diagnostics;
using System.Security.Cryptography;
using RemoteAgent.Security;

namespace RemoteAppClient.Tests.Agent;

/// <summary>A public key line made here is one ssh-keygen signs, and the certificate it makes is one OpenSSH
/// reads back - the whole path the agent's TPM key takes at the server and at the bastion.</summary>
public class SshKeyCertificateRoundTripTests
{
    [Fact]
    public void Openssh_signs_our_public_key_and_reads_the_certificate_back()
    {
        var keygen = FindSshKeygen();
        if (keygen is null) return;

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(false);
        var line = SshKeyEncoding.PublicKeyLine(SshKeyEncoding.PublicKeyBlob(p.Q.X!, p.Q.Y!), "remoteagent");

        var dir = Directory.CreateTempSubdirectory("sshcert-test");
        try
        {
            var ca = Path.Combine(dir.FullName, "ca");
            Run(keygen, "-q", "-t", "ed25519", "-N", "", "-f", ca, "-C", "ca");
            var pub = Path.Combine(dir.FullName, "key.pub");
            File.WriteAllText(pub, line + "\n");
            var (code, err) = Run(keygen, "-s", ca, "-I", "dev", "-n", "agent", "-V", "+3d", "-z", "7", pub);
            Assert.True(code == 0, "ssh-keygen -s refused our public key: " + err);

            var certLine = File.ReadAllText(Path.Combine(dir.FullName, "key-cert.pub")).Trim();
            var v = SshCertificate.Parse(certLine);
            Assert.NotNull(v);
            Assert.Equal("ecdsa-sha2-nistp256-cert-v01@openssh.com", v.Type);
            Assert.Equal(7UL, v.Serial);
            Assert.Equal("dev", v.KeyId);
            Assert.InRange((v.ValidBefore - v.ValidAfter).TotalDays, 2.9, 3.1);

            // ssh signs certificate authentication naming the certificate; the agent must find its plain key in it.
            var certBlob = Convert.FromBase64String(certLine.Split(' ')[1]);
            Assert.Equal(SshKeyEncoding.PublicKeyBlob(p.Q.X!, p.Q.Y!), SshKeyEncoding.PlainKeyOfCertificate(certBlob));
            Assert.Null(SshKeyEncoding.PlainKeyOfCertificate(SshKeyEncoding.PublicKeyBlob(p.Q.X!, p.Q.Y!))); // a plain key is not a certificate

            // Written the way the agent writes it, read the way ssh reads it.
            var written = Path.Combine(dir.FullName, "written-cert.pub");
            File.WriteAllText(written, certLine + "\n");
            var (code2, err2) = Run(keygen, "-L", "-f", written);
            Assert.True(code2 == 0, "ssh-keygen -L could not read the certificate as written: " + err2);
        }
        finally { dir.Delete(recursive: true); }
    }

    private static (int Code, string Err) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var err = proc.StandardError.ReadToEnd();
        proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, err);
    }

    private static string? FindSshKeygen()
    {
        foreach (var c in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh-keygen.exe"), "/usr/bin/ssh-keygen" })
            if (File.Exists(c)) return c;
        return null;
    }
}
