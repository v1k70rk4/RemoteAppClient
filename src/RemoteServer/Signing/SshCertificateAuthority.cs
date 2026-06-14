using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using RemoteServer.Configuration;
using L = RemoteServer.Localization.Strings;

namespace RemoteServer.Signing;

/// <summary>
/// Az agent SSH-publikus kulcsát aláírja a bástya SSH-CA-jával (ssh-keygen -s),
/// "agent" principal-lal és lejárattal. A bástya a TrustedUserCAKeys révén bízik a
/// CA-ban, így nem kell authorized_keys-t kezelni. Linux-only (ssh-keygen kell).
/// </summary>
public sealed class SshCertificateAuthority(IOptions<ServerOptions> options, ILogger<SshCertificateAuthority> logger)
{
    private readonly BastionOptions _opt = options.Value.Bastion;

    /// <summary>Visszaadja az aláírt SSH-certet (OpenSSH cert), vagy null ha nem sikerült.</summary>
    public async Task<string?> SignAsync(string sshPublicKey, string deviceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sshPublicKey))
            return null;
        if (!File.Exists(_opt.SshCaKeyPath))
        {
            logger.LogWarning(L.SshCertificateAuthority_001, _opt.SshCaKeyPath);
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
            psi.ArgumentList.Add("-I"); psi.ArgumentList.Add(deviceId);
            psi.ArgumentList.Add("-n"); psi.ArgumentList.Add(_opt.User);              // principal = bástya user
            psi.ArgumentList.Add("-V"); psi.ArgumentList.Add($"+{_opt.SshCertValidityDays}d");
            psi.ArgumentList.Add("-z"); psi.ArgumentList.Add(RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString());
            psi.ArgumentList.Add(pubPath);

            using var proc = Process.Start(psi)!;
            var err = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0 || !File.Exists(certPath))
            {
                logger.LogWarning(L.SshCertificateAuthority_002, deviceId, err);
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
