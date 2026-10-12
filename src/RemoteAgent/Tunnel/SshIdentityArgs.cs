using System.Diagnostics;
using RemoteAgent.Configuration;
using RemoteAgent.Security;

namespace RemoteAgent.Tunnel;

/// <summary>
/// How ssh.exe is told which tunnel key to use. A key in CNG: <c>-i</c> names the public key file and
/// SSH_AUTH_SOCK names the in-process agent's pipe, so OpenSSH takes the public half from the file and has the
/// agent sign (IdentitiesOnly still holds). A key file: <c>-i</c> names it, as always. The certificate goes
/// with either. Read at every connection start, so a rotation needs no restart; a caller may hand in another
/// identity to try (the rotation's probe of a key that is not current yet).
/// </summary>
public static class SshIdentityArgs
{
    public static void Apply(ProcessStartInfo psi, TunnelOptions options, SshIdentity.Snapshot? use = null)
    {
        var id = use ?? SshIdentity.Current;
        string keyArg, cert;
        if (id is { InAgent: true })
        {
            // -i names the public key file itself: OpenSSH loads it as a public identity, finds the same key in
            // the agent, and the certificate (CertificateFile) borrows that identity's signer. Naming a private
            // path that does not exist leaves no public identity to match, and ssh then has "no separate
            // private key for certificate" however many keys the agent offers.
            keyArg = id.PublicKeyPath;
            cert = id.CertificatePath;
            psi.Environment["SSH_AUTH_SOCK"] = SshKeyStore.PipePath;
        }
        else if (id is not null)
        {
            keyArg = id.PrivateKeyPath ?? options.PrivateKeyPath;
            cert = id.CertificatePath;
            psi.Environment.Remove("SSH_AUTH_SOCK");
        }
        else
        {
            keyArg = options.PrivateKeyPath;
            cert = options.CertificatePath;
            psi.Environment.Remove("SSH_AUTH_SOCK");
        }
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(keyArg);
        if (!string.IsNullOrWhiteSpace(cert))
        {
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add($"CertificateFile=\"{cert}\"");
        }
    }
}
