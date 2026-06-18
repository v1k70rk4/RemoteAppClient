using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RemoteServer.Data;
using RemoteServer.Data.Entities;

namespace RemoteServer.Services;

/// <summary>
/// Session management: token creation (raw token only goes to client, DB stores hash),
/// validation (expiry + revocation), and revocation. Session carries user identity for
/// console calls; transport is provided by the device SSH tunnel and reachable only from fleet devices.
/// </summary>
public sealed class AuthService(AppDbContext db)
{
    public static readonly TimeSpan SessionTtl = TimeSpan.FromHours(8); // one work day; the operator SSH cert is minted to match (OperatorCertValidityHours)

    /// <summary>How long a "remember this device" 2FA trust stays valid before TOTP is required again.</summary>
    public static readonly TimeSpan TrustTtl = TimeSpan.FromDays(90);

    public async Task<string> CreateSessionAsync(User user, CancellationToken ct)
    {
        var raw = Base64Url(RandomNumberGenerator.GetBytes(32));
        db.UserSessions.Add(new UserSession
        {
            UserId = user.Id,
            TokenHash = HashToken(raw),
            ExpiresAt = DateTimeOffset.UtcNow + SessionTtl,
        });
        await db.SaveChangesAsync(ct);
        return raw;
    }

    /// <summary>True if the raw token matches a live (not revoked/expired) device trust for the user; updates last-used.</summary>
    public async Task<bool> IsDeviceTrustedAsync(string? rawToken, Guid userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return false;
        var hash = HashToken(rawToken);
        var trust = await db.DeviceTrusts.FirstOrDefaultAsync(t => t.TokenHash == hash && t.UserId == userId, ct);
        if (trust is null || trust.RevokedAt is not null || trust.ExpiresAt < DateTimeOffset.UtcNow) return false;
        trust.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Issues a "remember this device" trust token (raw goes to the client; DB stores only its hash).</summary>
    public async Task<string> IssueDeviceTrustAsync(Guid userId, string? deviceName, CancellationToken ct)
    {
        var raw = Base64Url(RandomNumberGenerator.GetBytes(32));
        db.DeviceTrusts.Add(new DeviceTrust
        {
            UserId = userId, TokenHash = HashToken(raw), DeviceName = deviceName,
            ExpiresAt = DateTimeOffset.UtcNow + TrustTtl,
        });
        await db.SaveChangesAsync(ct);
        return raw;
    }

    /// <summary>User with roles plus session for a valid token; otherwise null.</summary>
    public async Task<(User User, UserSession Session)?> ValidateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = HashToken(token);

        var session = await db.UserSessions
            .Include(s => s.User).ThenInclude(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(s => s.TokenHash == hash, ct);

        if (session is null || session.RevokedAt is not null || session.ExpiresAt < DateTimeOffset.UtcNow)
            return null;
        if (!session.User.IsActive)
            return null;

        session.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return (session.User, session);
    }

    public async Task RevokeAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        var hash = HashToken(token);
        var session = await db.UserSessions.FirstOrDefaultAsync(s => s.TokenHash == hash, ct);
        if (session is not null && session.RevokedAt is null)
        {
            session.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Revokes all live sessions of a user for lockout or permission removal.</summary>
    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct)
    {
        var live = await db.UserSessions.Where(s => s.UserId == userId && s.RevokedAt == null).ToListAsync(ct);
        foreach (var s in live) s.RevokedAt = DateTimeOffset.UtcNow;

        // A full sign-out also drops "remember this device" trusts, so TOTP is required again next time.
        var trusts = await db.DeviceTrusts.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in trusts) t.RevokedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public static string RoleOf(User u) =>
        u.UserRoles.Any(r => r.Role.Name == "admin") ? "admin"
        : u.UserRoles.FirstOrDefault()?.Role.Name ?? "operator";

    public static bool IsAdmin(User u) => u.UserRoles.Any(r => r.Role.Name == "admin");

    /// <summary>User grants: sets of granted group IDs and device IDs.</summary>
    public async Task<(HashSet<Guid> GroupIds, HashSet<Guid> DeviceIds)> GrantsAsync(Guid userId, CancellationToken ct)
    {
        var grants = await db.UserGrants.Where(g => g.UserId == userId).ToListAsync(ct);
        return (grants.Where(g => g.GroupId != null).Select(g => g.GroupId!.Value).ToHashSet(),
                grants.Where(g => g.DeviceId != null).Select(g => g.DeviceId!.Value).ToHashSet());
    }

    /// <summary>Whether an operator can access the device through group or device grant.</summary>
    public static bool CanAccessDevice(Device d, HashSet<Guid> groupIds, HashSet<Guid> deviceIds) =>
        (d.GroupId is { } g && groupIds.Contains(g)) || deviceIds.Contains(d.Id);

    private static string HashToken(string raw) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));

    private static string Base64Url(byte[] b) =>
        Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
