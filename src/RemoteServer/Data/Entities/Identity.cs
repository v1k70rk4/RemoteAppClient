namespace RemoteServer.Data.Entities;

/// <summary>Operátor/felhasználó. Jelszó (Argon2id) + kötelező TOTP 2FA a konzolhoz.</summary>
public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string? Email { get; set; }

    /// <summary>Megjelenítendő név (pl. „Révész Viktor"). Ez látszik majd a hozzájárulás-kérésben és a logban.</summary>
    public string? Name { get; set; }

    /// <summary>Argon2id hash. Soha nem plaintext.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>TOTP titok (SecretProtector-rel TITKOSÍTVA tárolva). Null, amíg nincs beállítva.</summary>
    public string? TotpSecret { get; set; }

    /// <summary>Igaz, ha a TOTP-titok már meg lett erősítve egy érvényes kóddal (enroll kész).</summary>
    public bool TotpConfirmed { get; set; }

    /// <summary>Igaz, ha a következő belépéskor kötelező jelszót cserélni (pl. admin által adott ideiglenes jelszó).</summary>
    public bool MustChangePassword { get; set; } = true;

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PasswordChangedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>Jelszó-emlékeztető kód SHA-256 hash-e (hex). Null, ha nincs aktív kód.</summary>
    public string? ResetCodeHash { get; set; }
    public DateTimeOffset? ResetCodeExpiresAt { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = [];
    public ICollection<UserGrant> Grants { get; set; } = [];
}

/// <summary>
/// Windows Hello (passkey-stílusú) hitelesítő egy userhez+géphez. A privát kulcs a gép TPM-jében,
/// Hello-val (ujjlenyomat/PIN) védve; a szerver CSAK a publikus kulcsot tárolja. Visszavonható.
/// Belépés: a kliens egy szerver-challenge-et ír alá a Hello-kulccsal, a szerver a pub-kulccsal ellenőrzi.
/// </summary>
public sealed class HelloCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>A publikus kulcs (X.509 SubjectPublicKeyInfo, base64).</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>Megjelenítendő eszköznév (pl. a gép hostname-je).</summary>
    public string DeviceName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>Szerep (admin / viewer). RBAC tier.</summary>
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

/// <summary>
/// Hozzáférés-grant egy usernek: vagy egy CSOPORTRA (annak minden gépe), vagy egy konkrét GÉPRE.
/// Admin szerep felülírja (mindenhez fér). Viewer csak a grantjait látja/éri el.
/// </summary>
public sealed class UserGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Csoport-szintű grant (a csoport összes gépe). Null, ha gép-szintű.</summary>
    public Guid? GroupId { get; set; }

    /// <summary>Gép-szintű grant. Null, ha csoport-szintű.</summary>
    public Guid? DeviceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Szerver-oldali session (visszavonható). A nyers tokent csak a kliens kapja meg;
/// a DB-ben a token HASH-e van. Lejárat + explicit visszavonás (kitiltás).
/// </summary>
public sealed class UserSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>A session-token SHA-256 hash-e (hex). A nyers token sosem kerül DB-be.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}
