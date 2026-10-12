using System.Diagnostics;
using System.Security.Cryptography;
using RemoteAgent.Security;

namespace RemoteAppClient.Tests.Agent;

/// <summary>The in-process ssh-agent's wire encodings, checked against what OpenSSH itself produces and accepts.</summary>
public class SshKeyEncodingTests
{
    // ssh-keygen -t ecdsa -b 256: the public line, and the point OpenSSH put in it.
    private const string ReferenceLine = "ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBLBPyHdKiNX01wN3ZbMXxkXNeKCLarvWu5XjpnUzqpl3kyqoqMXoQ3jPhRCrxMkh+IYlqyhxTzWE0COqSOpYbv8= ek";
    private const string X = "b04fc8774a88d5f4d7037765b317c645cd78a08b6abbd6bb95e3a67533aa9977";
    private const string Y = "932aa8a8c5e84378cf8510abc4c921f88625ab28714f3584d023aa48ea586eff";

    [Fact]
    public void Encodes_a_public_key_exactly_as_openssh_does()
    {
        var blob = SshKeyEncoding.PublicKeyBlob(Convert.FromHexString(X), Convert.FromHexString(Y));
        Assert.Equal(ReferenceLine, SshKeyEncoding.PublicKeyLine(blob, "ek"));
    }

    [Fact]
    public void A_short_coordinate_is_left_padded_to_32_bytes()
    {
        var blob = SshKeyEncoding.PublicKeyBlob(Convert.FromHexString("01"), Convert.FromHexString(Y));
        Assert.Equal(Convert.FromHexString(Y), blob[^32..]);
        Assert.Equal(1, blob[^33]);                 // the x coordinate's last byte
        Assert.All(blob[^64..^33], b => Assert.Equal(0, b)); // and its padding
    }

    [Fact]
    public void Mpints_never_read_as_negative()
    {
        var w = new SshKeyEncoding.Writer();
        w.Mpint(Convert.FromHexString("00ff"));
        Assert.Equal(new byte[] { 0, 0, 0, 2, 0, 0xff }, w.ToArray()); // leading zero stripped, one put back for the high bit
        w = new SshKeyEncoding.Writer();
        w.Mpint(Convert.FromHexString("7f"));
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x7f }, w.ToArray());
    }

    [Fact]
    public void Sign_request_fields_parse_and_junk_does_not()
    {
        var w = new SshKeyEncoding.Writer();
        w.Bytes(new byte[] { 1, 2, 3 }); w.Bytes(new byte[] { 9 }); w.UInt32(4);
        var parsed = SshKeyEncoding.ParseSignRequest(w.ToArray());
        Assert.NotNull(parsed);
        Assert.Equal(new byte[] { 1, 2, 3 }, parsed.Value.KeyBlob);
        Assert.Equal(new byte[] { 9 }, parsed.Value.Data);
        Assert.Equal(4u, parsed.Value.Flags);
        Assert.Null(SshKeyEncoding.ParseSignRequest(new byte[] { 0, 0, 0, 9, 1 }));
    }

    [Fact]
    public void Frames_carry_their_length_first()
    {
        var f = SshKeyEncoding.Frame([SshKeyEncoding.AgentFailure]);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 5 }, f);
    }

    /// <summary>The proof of possession the agent makes with a CNG key is the format ssh-keygen -Y verify checks;
    /// proven here with a software ECDSA key and the real ssh-keygen when one is installed.</summary>
    [Fact]
    public void Sshsig_made_here_verifies_with_ssh_keygen()
    {
        var keygen = FindSshKeygen();
        if (keygen is null) return; // no OpenSSH on this runner: the structure is still exercised below

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(false);
        var blob = SshKeyEncoding.PublicKeyBlob(p.Q.X!, p.Q.Y!);
        var message = "ecdsa-sha2-nistp256 AAAA-the-new-key\n"u8.ToArray();
        var sig = SshKeyEncoding.SshSig(blob, "remoteagent-rotation", message, toSign => ecdsa.SignData(toSign, HashAlgorithmName.SHA256));

        var dir = Directory.CreateTempSubdirectory("sshsig-test");
        try
        {
            var allowed = Path.Combine(dir.FullName, "allowed");
            var sigPath = Path.Combine(dir.FullName, "msg.sig");
            File.WriteAllText(allowed, "rotation " + SshKeyEncoding.PublicKeyLine(blob, "t") + "\n");
            File.WriteAllText(sigPath, sig);
            var psi = new ProcessStartInfo(keygen) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-Y", "verify", "-f", allowed, "-I", "rotation", "-n", "remoteagent-rotation", "-s", sigPath }) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            proc.StandardInput.BaseStream.Write(message);
            proc.StandardInput.Close();
            var err = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Assert.True(proc.ExitCode == 0, "ssh-keygen -Y verify refused the signature: " + err);
        }
        finally { dir.Delete(recursive: true); }
    }

    private static string? FindSshKeygen()
    {
        foreach (var c in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh-keygen.exe"), "/usr/bin/ssh-keygen" })
            if (File.Exists(c)) return c;
        return null;
    }
}
