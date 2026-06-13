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
    /// <summary>Ha igaz (és van e-mail-szolgáltatás + a usernek e-mailje): reset-kódot küld e-mailben.</summary>
    [JsonPropertyName("emailCode")] public bool EmailCode { get; set; }
}

public sealed class CreateUserResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("tempPassword")] public string TempPassword { get; set; } = string.Empty;
    /// <summary>Jelszó-helyreállítási token (a helyreállító oldalon írható be). Az admin felület ezt mutatja.</summary>
    [JsonPropertyName("resetCode")] public string ResetCode { get; set; } = string.Empty;
    /// <summary>Igaz, ha a tokent sikerült e-mailben is kiküldeni.</summary>
    [JsonPropertyName("emailSent")] public bool EmailSent { get; set; }
}

/// <summary>Jelszó-emlékeztető: kód kérése (felhasználónév + e-mail). Anti-enumeration: a válasz mindig OK.</summary>
public sealed class PasswordCodeRequest
{
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
}

/// <summary>Jelszó-emlékeztető: új jelszó beállítása a kapott kóddal.</summary>
public sealed class PasswordResetRequest
{
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
    [JsonPropertyName("newPassword")] public string NewPassword { get; set; } = string.Empty;
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
}

/// <summary>User módosítása (név / szerep / aktív). A null mezők változatlanok.</summary>
public sealed class UserUpdate
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
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
