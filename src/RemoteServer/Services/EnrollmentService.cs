using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteAgent.Enrollment;
using RemoteServer.Data;
using RemoteServer.Data.Entities;
using RemoteServer.Signing;
using L = RemoteServer.Localization.Strings;

namespace RemoteServer.Services;

/// <summary>
/// Enrollment flow: token validation (hash + expiry + usage), CSR signing by the CA,
/// <see cref="Device"/> creation, and audit trail. The server assigns the device ID,
/// not the client.
/// </summary>
public sealed class EnrollmentService(
    AppDbContext db,
    CertificateAuthority ca,
    CommandSigner signer,
    SshCertificateAuthority sshCa,
    Microsoft.Extensions.Options.IOptions<Configuration.ServerOptions> serverOptions,
    ILogger<EnrollmentService> logger)
{
    private readonly Configuration.BastionOptions _bastion = serverOptions.Value.Bastion;

    public sealed record Result(EnrollResponse? Response, string? ErrorCode);

    public async Task<Result> EnrollWithTokenAsync(EnrollRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Token) || string.IsNullOrWhiteSpace(req.Csr))
            return new Result(null, "invalid_request");

        var hash = HashToken(req.Token);
        var token = await db.EnrollmentTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null)
            return new Result(null, "invalid_token");
        if (token.RevokedAt is not null)
            return new Result(null, "token_revoked");
        if (token.ExpiresAt is { } exp && exp < DateTimeOffset.UtcNow)
            return new Result(null, "token_expired");
        if (token.UseCount >= token.MaxUses)
            return new Result(null, "token_spent");

        var deviceId = Guid.NewGuid().ToString("N");

        string certPem;
        string thumbprint;
        try
        {
            certPem = ca.SignClientCsr(req.Csr, deviceId);
            using var leaf = X509Certificate2.CreateFromPem(certPem);
            thumbprint = leaf.Thumbprint;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, L.EnrollmentService_CSRSigningFailedDeviceDevice, deviceId);
            return new Result(null, "bad_csr");
        }

        // Sign the SSH key with the bastion CA, best effort but required for tunnels.
        var sshCert = await sshCa.SignAsync(req.SshPublicKey, deviceId, ct);

        // Allocate a stable unique bastion port from the range, using the lowest free one.
        var usedPorts = (await db.Devices
            .Where(d => d.TunnelPort != null)
            .Select(d => d.TunnelPort!.Value)
            .ToListAsync(ct)).ToHashSet();
        int? tunnelPort = null;
        for (int p = _bastion.TunnelPortMin; p < _bastion.TunnelPortMax; p++)
            if (!usedPorts.Contains(p)) { tunnelPort = p; break; }
        if (tunnelPort is null)
            return new Result(null, "no_free_port");

        var device = new Device
        {
            DeviceId = deviceId,
            Hostname = req.Hostname,
            GroupId = token.GroupId,
            // Admin one-time token -> Approved immediately; site/bootstrap token -> Pending approval.
            Status = token.AutoApprove ? DeviceStatus.Approved : DeviceStatus.Pending,
            CertThumbprint = thumbprint,
            SshPublicKey = req.SshPublicKey,
            TunnelPort = tunnelPort,
            EnrolledAt = DateTimeOffset.UtcNow,
        };
        db.Devices.Add(device);

        token.UseCount++;
        token.UsedAt = DateTimeOffset.UtcNow;
        token.UsedByDeviceId = device.Id;

        db.AuditLogs.Add(new AuditLog
        {
            Actor = "system",
            Action = "device.enrolled",
            TargetDeviceId = device.Id,
            DetailJson = JsonSerializer.Serialize(new { hostname = req.Hostname }),
        });

        await db.SaveChangesAsync(ct);
        logger.LogInformation(L.EnrollmentService_DeviceEnrolledDeviceHost, deviceId, req.Hostname);

        return new Result(new EnrollResponse
        {
            DeviceId = deviceId,
            Certificate = certPem,
            CaCertificate = ca.CaCertificatePem,
            CommandSigningPublicKey = signer.PublicKeySpkiBase64,
            SshCertificate = sshCert ?? string.Empty,
            BastionHost = _bastion.Host,
            BastionPort = _bastion.Port,
            BastionUser = _bastion.User,
            BastionHostKey = _bastion.HostKey,
        }, null);
    }

    /// <summary>
    /// Creates a new enrollment token. Returns the raw token, visible only now because the DB
    /// stores a hash, plus the created entity so callers can attach MSI file name after build.
    /// </summary>
    public async Task<(string Raw, EnrollmentToken Token)> CreateTokenAsync(
        int maxUses, int? expiresInHours, Guid? groupId, string? note, CancellationToken ct, bool autoApprove = true)
    {
        var raw = Base64Url(RandomNumberGenerator.GetBytes(24));
        var token = new EnrollmentToken
        {
            TokenHash = HashToken(raw),
            MaxUses = maxUses < 1 ? 1 : maxUses,
            ExpiresAt = expiresInHours is { } h ? DateTimeOffset.UtcNow.AddHours(h) : null,
            GroupId = groupId,
            Note = note,
            AutoApprove = autoApprove,
        };
        db.EnrollmentTokens.Add(token);
        await db.SaveChangesAsync(ct);
        return (raw, token);
    }

    private static string HashToken(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
