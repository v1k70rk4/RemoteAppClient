using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteAgent.Enrollment;
using RemoteServer.Data;
using RemoteServer.Data.Entities;
using RemoteServer.Signing;

namespace RemoteServer.Services;

/// <summary>
/// A beléptetés logikája: token-validáció (hash + lejárat + felhasználás), a CSR
/// aláírása a CA-val, a <see cref="Device"/> létrehozása, és audit-nyom. A device-
/// azonosítót a szerver osztja (nem a kliens).
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
            logger.LogWarning(ex, "CSR aláírása sikertelen (device {Device}).", deviceId);
            return new Result(null, "bad_csr");
        }

        // Az SSH-kulcs aláírása a bástya-CA-val (best effort; tunnelhez kell).
        var sshCert = await sshCa.SignAsync(req.SshPublicKey, deviceId, ct);

        // Stabil, egyedi bástya-port kiosztása a tartományból (a legalacsonyabb szabad).
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
            // Admin egyszer-használatos token → azonnal Approved; site/bootstrap token → Pending (jóváhagyásra vár).
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
        logger.LogInformation("Gép beléptetve: {Device} ({Host})", deviceId, req.Hostname);

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

    /// <summary>Új beléptető token. A NYERS tokent adja vissza (csak most látható); a DB-ben hash van.</summary>
    public async Task<string> CreateTokenAsync(int maxUses, int? expiresInHours, Guid? groupId, string? note, CancellationToken ct, bool autoApprove = true)
    {
        var raw = Base64Url(RandomNumberGenerator.GetBytes(24));
        db.EnrollmentTokens.Add(new EnrollmentToken
        {
            TokenHash = HashToken(raw),
            MaxUses = maxUses < 1 ? 1 : maxUses,
            ExpiresAt = expiresInHours is { } h ? DateTimeOffset.UtcNow.AddHours(h) : null,
            GroupId = groupId,
            Note = note,
            AutoApprove = autoApprove,
        });
        await db.SaveChangesAsync(ct);
        return raw;
    }

    private static string HashToken(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
