using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RemoteServer.Data;
using RemoteServer.Data.Entities;

namespace RemoteServer.Services;

/// <summary>
/// Session-kezelés: token létrehozás (a nyers tokent csak a kliens kapja, a DB-ben hash van),
/// validálás (lejárat + visszavonás), visszavonás. A session a felhasználó identitását hordozza
/// a konzol-hívásokhoz; a transportot a gép SSH-tunnelje adja (csak fleet-gépről érhető el).
/// </summary>
public sealed class AuthService(AppDbContext db)
{
    public static readonly TimeSpan SessionTtl = TimeSpan.FromHours(12);

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

    /// <summary>A tokenhez tartozó user (szerepekkel) + session, ha érvényes; különben null.</summary>
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

    /// <summary>Egy user összes élő sessionjének visszavonása (kitiltás/jogmegvonás).</summary>
    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct)
    {
        var live = await db.UserSessions.Where(s => s.UserId == userId && s.RevokedAt == null).ToListAsync(ct);
        foreach (var s in live) s.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public static string RoleOf(User u) =>
        u.UserRoles.Any(r => r.Role.Name == "admin") ? "admin"
        : u.UserRoles.FirstOrDefault()?.Role.Name ?? "operator";

    public static bool IsAdmin(User u) => u.UserRoles.Any(r => r.Role.Name == "admin");

    /// <summary>Egy user grantjai: a grantolt csoport-Id-k és gép-Id-k halmaza.</summary>
    public async Task<(HashSet<Guid> GroupIds, HashSet<Guid> DeviceIds)> GrantsAsync(Guid userId, CancellationToken ct)
    {
        var grants = await db.UserGrants.Where(g => g.UserId == userId).ToListAsync(ct);
        return (grants.Where(g => g.GroupId != null).Select(g => g.GroupId!.Value).ToHashSet(),
                grants.Where(g => g.DeviceId != null).Select(g => g.DeviceId!.Value).ToHashSet());
    }

    /// <summary>Hozzáfér-e az operator az adott géphez (csoport-grant VAGY gép-grant alapján)?</summary>
    public static bool CanAccessDevice(Device d, HashSet<Guid> groupIds, HashSet<Guid> deviceIds) =>
        (d.GroupId is { } g && groupIds.Contains(g)) || deviceIds.Contains(d.Id);

    private static string HashToken(string raw) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));

    private static string Base64Url(byte[] b) =>
        Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
