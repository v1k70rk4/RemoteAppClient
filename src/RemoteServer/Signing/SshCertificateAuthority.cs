using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using RemoteServer.Configuration;
using L = RemoteServer.Localization.Strings;

namespace RemoteServer.Signing;

/// <summary>
/// Signs agent SSH public keys with the bastion SSH CA (ssh-keygen -s), using the "agent"
/// principal and expiry. Bastion trusts the CA through TrustedUserCAKeys, so authorized_keys
/// management is unnecessary. Linux-only because ssh-keygen is required.
/// </summary>
public sealed class SshCertificateAuthority(IOptions<ServerOptions> options, ILogger<SshCertificateAuthority> logger)
{
    private readonly BastionOptions _opt = options.Value.Bastion;

    /// <summary>Signs an agent SSH key (long-lived, "agent" principal). Returns the OpenSSH cert or null.</summary>
    public Task<string?> SignAsync(string sshPublicKey, string deviceId, CancellationToken ct)
        => SignInternalAsync(sshPublicKey, deviceId, $"+{_opt.SshCertValidityDays}d", deviceId, ct);

    /// <summary>
    /// Signs a short-lived operator SSH key for the keyless Linux console. Same "agent" principal and
    /// forwarding rights as an agent cert (so the bastion needs no change), but a short validity and an
    /// "operator:&lt;username&gt;" key id for audit. Returns the OpenSSH cert or null.
    /// </summary>
    public Task<string?> SignOperatorAsync(string sshPublicKey, string username, CancellationToken ct)
        => SignInternalAsync(sshPublicKey, "operator:" + username, $"+{_opt.OperatorCertValidityHours}h", username, ct);

    private async Task<string?> SignInternalAsync(string sshPublicKey, string keyId, string validity, string logId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sshPublicKey))
            return null;
        if (!File.Exists(_opt.SshCaKeyPath))
        {
            logger.LogWarning(L.SshCertificateAuthority_SSHCAKeyNotFound, _opt.SshCaKeyPath);
            return null;
        }

        var dir = Path.Combine(Path.GetTempPath(), "ra_ssh_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var pubPath = Path.Combine(dir, "key.pub");
        var certPath = Path.Combine(dir, "key-cert.pub");
        try
        {
            await File.WriteAllTextAsync(pubPath, sshPublicKey.Trim() + "\n", ct);

            var psi = new ProcessStartInfo("ssh-keygen")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-s"); psi.ArgumentList.Add(_opt.SshCaKeyPath);
            psi.ArgumentList.Add("-I"); psi.ArgumentList.Add(keyId);
            psi.ArgumentList.Add("-n"); psi.ArgumentList.Add(_opt.User);              // principal = bastion user
            psi.ArgumentList.Add("-V"); psi.ArgumentList.Add(validity);
            psi.ArgumentList.Add("-z"); psi.ArgumentList.Add(RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString());
            psi.ArgumentList.Add(pubPath);

            using var proc = Process.Start(psi)!;
            var err = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0 || !File.Exists(certPath))
            {
                logger.LogWarning(L.SshCertificateAuthority_SshKeygenSigningFailedDevice, logId, err);
                return null;
            }
            return (await File.ReadAllTextAsync(certPath, ct)).Trim();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
