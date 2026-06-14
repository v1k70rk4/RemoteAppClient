namespace RemoteServer.Data.Entities;

/// <summary>Operator/user. Password uses Argon2id and console access requires TOTP 2FA.</summary>
public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string? Email { get; set; }

    /// <summary>Display name shown in consent prompts and logs.</summary>
    public string? Name { get; set; }

    /// <summary>Argon2id hash. Never plaintext.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>TOTP secret stored encrypted with SecretProtector. Null until configured.</summary>
    public string? TotpSecret { get; set; }

    /// <summary>True when the TOTP secret has been confirmed with a valid code.</summary>
    public bool TotpConfirmed { get; set; }

    /// <summary>True when next sign-in must change password, for example after an admin temporary password.</summary>
    public bool MustChangePassword { get; set; } = true;

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PasswordChangedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>Password recovery code SHA-256 hash (hex). Null when no active code exists.</summary>
    public string? ResetCodeHash { get; set; }
    public DateTimeOffset? ResetCodeExpiresAt { get; set; }

    /// <summary>
    /// Per-operator TightVNC viewer scale preference: "auto" (fit to window) or a percent string "1".."400".
    /// Null/empty means auto. Stored on the account so it roams to any console the operator signs in from.
    /// </summary>
    public string? ViewerScale { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = [];
    public ICollection<UserGrant> Grants { get; set; } = [];
}

/// <summary>
/// Windows Hello passkey-style credential for a user and device. The private key lives in the
/// device TPM and is protected by Hello (fingerprint/PIN). The server stores only the public key.
/// Sign-in: the client signs a server challenge with the Hello key, and the server verifies it.
/// </summary>
public sealed class HelloCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Public key (X.509 SubjectPublicKeyInfo, base64).</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>Display device name, usually the hostname.</summary>
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

/// <summary>User-to-role join table.</summary>
public sealed class UserRole
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
}

/// <summary>
/// Access grant for a user: either a group (all devices in it) or a concrete device.
/// Admin role overrides grants and can access everything. Operators see and access only their grants.
/// </summary>
public sealed class UserGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Group-level grant covering all devices in the group. Null for device-level grant.</summary>
    public Guid? GroupId { get; set; }

    /// <summary>Device-level grant. Null for group-level grant.</summary>
    public Guid? DeviceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Server-side revocable session. The raw token is returned only to the client; the DB stores
/// only its hash. Sessions expire and can be explicitly revoked for lockout/permission changes.
/// </summary>
public sealed class UserSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Session token SHA-256 hash (hex). The raw token is never stored in DB.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}
