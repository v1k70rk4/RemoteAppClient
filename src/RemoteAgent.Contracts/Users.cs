using System.Text.Json.Serialization;

namespace RemoteAgent.Admin;

/// <summary>A user in the admin user-management list.</summary>
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

    /// <summary>Number of active Windows Hello credentials, meaning how many devices can sign in passwordlessly.</summary>
    [JsonPropertyName("helloCount")] public int HelloCount { get; set; }

    /// <summary>True when this account may sign in to the Linux operator console (mints a keyless SSH cert).</summary>
    [JsonPropertyName("keylessOperator")] public bool KeylessOperator { get; set; }
}

/// <summary>Creates a new user (admin). The server generates a temporary password.</summary>
public sealed class CreateUserRequest
{
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("role")] public string Role { get; set; } = "operator"; // admin | operator
    /// <summary>When true and email is configured for the user, sends the reset code by email.</summary>
    [JsonPropertyName("emailCode")] public bool EmailCode { get; set; }
}

public sealed class CreateUserResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("tempPassword")] public string TempPassword { get; set; } = string.Empty;
    /// <summary>Password recovery token entered on the recovery page and shown by the admin UI.</summary>
    [JsonPropertyName("resetCode")] public string ResetCode { get; set; } = string.Empty;
    /// <summary>True when the token was also sent by email.</summary>
    [JsonPropertyName("emailSent")] public bool EmailSent { get; set; }
}

/// <summary>Password recovery: request a code by username and email. Anti-enumeration: the response is always OK.</summary>
public sealed class PasswordCodeRequest
{
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
    /// <summary>The requesting client's language (hu/en); the server sends the recovery email in this language.</summary>
    [JsonPropertyName("language")] public string? Language { get; set; }
}

/// <summary>Password recovery: set a new password with the received code.</summary>
public sealed class PasswordResetRequest
{
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
    [JsonPropertyName("newPassword")] public string NewPassword { get; set; } = string.Empty;
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
}

/// <summary>Updates a user (name / role / active state / Linux-console access). Null fields are left unchanged.</summary>
public sealed class UserUpdate
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("isActive")] public bool? IsActive { get; set; }
    [JsonPropertyName("keylessOperator")] public bool? KeylessOperator { get; set; }
}

/// <summary>An access grant for a user, targeting either a group or a device.</summary>
public sealed class GrantInfo
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("groupId")] public Guid? GroupId { get; set; }
    [JsonPropertyName("groupName")] public string? GroupName { get; set; }
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
    [JsonPropertyName("deviceHostname")] public string? DeviceHostname { get; set; }
}

/// <summary>Adds a grant for either a group or a device; exactly one should be set.</summary>
public sealed class GrantRequest
{
    [JsonPropertyName("groupId")] public Guid? GroupId { get; set; }
    /// <summary>The device ID known by the agent and client.</summary>
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
}
