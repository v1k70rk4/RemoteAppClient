namespace RemoteServer.Data.Entities;

/// <summary>Admin/operátor felhasználó. Jelszó + TOTP 2FA az interaktív enrollhoz és a konzolhoz.</summary>
public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string? Email { get; set; }

    /// <summary>Argon2id hash. Soha nem plaintext.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>TOTP titok (titkosítva tárolva). Null, amíg a 2FA nincs beállítva.</summary>
    public string? TotpSecret { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<UserRole> UserRoles { get; set; } = [];
}

/// <summary>Szerep (admin / operator / viewer). RBAC.</summary>
public sealed class Role
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    public ICollection<UserRole> UserRoles { get; set; } = [];
}

/// <summary>User↔Role kapcsolótábla.</summary>
public sealed class UserRole
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
}
