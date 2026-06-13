using System.Text.Json.Serialization;

namespace RemoteAgent.Admin;

/// <summary>Egy felhasználó az admin user-kezelő listában.</summary>
public sealed class UserInfo
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
    [JsonPropertyName("mustChangePassword")] public bool MustChangePassword { get; set; }
    [JsonPropertyName("totpConfirmed")] public bool TotpConfirmed { get; set; }
    [JsonPropertyName("lastLoginAt")] public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>Aktív Windows Hello hitelesítők száma (ennyi gépről léphet be passwordless).</summary>
    [JsonPropertyName("helloCount")] public int HelloCount { get; set; }
}

/// <summary>Új user létrehozása (admin). A szerver ideiglenes jelszót generál.</summary>
public sealed class CreateUserRequest
{
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("role")] public string Role { get; set; } = "operator"; // admin | operator
}

public sealed class CreateUserResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("tempPassword")] public string TempPassword { get; set; } = string.Empty;
}

/// <summary>User módosítása (név / szerep / aktív). A null mezők változatlanok.</summary>
public sealed class UserUpdate
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("isActive")] public bool? IsActive { get; set; }
}

/// <summary>Egy hozzáférés-grant (csoport VAGY gép) egy userhez.</summary>
public sealed class GrantInfo
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("groupId")] public Guid? GroupId { get; set; }
    [JsonPropertyName("groupName")] public string? GroupName { get; set; }
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
    [JsonPropertyName("deviceHostname")] public string? DeviceHostname { get; set; }
}

/// <summary>Grant hozzáadása: csoport VAGY gép (az egyik kitöltve).</summary>
public sealed class GrantRequest
{
    [JsonPropertyName("groupId")] public Guid? GroupId { get; set; }
    /// <summary>A gép agent-oldali DeviceId-ja (a kliens ezt ismeri).</summary>
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
}
