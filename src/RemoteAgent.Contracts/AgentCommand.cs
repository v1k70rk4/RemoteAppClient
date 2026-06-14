using System.Text.Json.Serialization;
using RemoteAgent.Telemetry;

namespace RemoteAgent.Commands;

/// <summary>
/// Agent-to-server message sent back over the persistent WSS channel, for example an access result.
/// The server matches it to the issued command by <see cref="Nonce"/>, and the console waits for it.
/// </summary>
public sealed class AgentUplinkMessage
{
    /// <summary>Message type, for example "access-result".</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    /// <summary>Nonce of the issued command, used for correlation.</summary>
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = string.Empty;
    /// <summary>Outcome: "auto" | "granted" | "denied" | "timeout" | "no-user" | "locked".</summary>
    [JsonPropertyName("outcome")] public string Outcome { get; set; } = string.Empty;
}

/// <summary>
/// Signed command sent by the server. <see cref="Signature"/> is an ECDSA signature
/// over the canonical payload, see <see cref="CommandSignature"/>.
/// <see cref="Nonce"/> and <see cref="IssuedAt"/> provide replay protection.
/// Shared DTO: client and server use the same type so their contracts cannot drift.
/// </summary>
public sealed class AgentCommand
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Unique command ID (GUID), also used as the replay-cache nonce.</summary>
    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;

    /// <summary>Issue time as Unix epoch seconds.</summary>
    [JsonPropertyName("iat")]
    public long IssuedAt { get; set; }

    /// <summary>Command-specific data, for example which server-side port to forward to.</summary>
    [JsonPropertyName("data")]
    public CommandData? Data { get; set; }

    /// <summary>Signature over the canonical form of the fields above (Base64).</summary>
    [JsonPropertyName("sig")]
    public string Signature { get; set; } = string.Empty;
}

public sealed class CommandData
{
    /// <summary>For tunnel commands: which remote bastion-side port to forward.</summary>
    [JsonPropertyName("remotePort")]
    public int RemotePort { get; set; }

    // For update commands: package version, download URL, and SHA-256 hash.
    // All of them are covered by the signature, see CommandSignature.Canonicalize.
    [JsonPropertyName("version")]
    public string? UpdateVersion { get; set; }

    [JsonPropertyName("url")]
    public string? UpdateUrl { get; set; }

    [JsonPropertyName("sha256")]
    public string? UpdateSha256 { get; set; }

    /// <summary>
    /// Which component to update: "agent" (default) or "updater"/"helper".
    /// The agent replaces the updater package because the Helper cannot replace its own running executable;
    /// the Helper replaces the agent package. This field is covered by the signature.
    /// </summary>
    [JsonPropertyName("target")]
    public string? UpdateTarget { get; set; }

    // Access policy for opening tunnels, filled by the server. It is part of the signed canonical payload,
    // so the device only accepts an authenticated policy from the server.
    // Missing (null) means the legacy behavior: no consent required, unattended allowed.
    /// <summary>Whether the interactive user at the device must approve the connection. null = no.</summary>
    [JsonPropertyName("consentRequired")]
    public bool? ConsentRequired { get; set; }

    /// <summary>Whether unattended access is allowed when nobody is signed in. null = yes.</summary>
    [JsonPropertyName("unattendedAllowed")]
    public bool? UnattendedAllowed { get; set; }

    // For "message" commands (Messages tab). Only signed/canonicalized for the message command type,
    // so existing command signatures stay unchanged.
    /// <summary>"availability" (Yes/No "may I connect now") or "text" (plain message + OK).</summary>
    [JsonPropertyName("messageKind")] public string? MessageKind { get; set; }
    /// <summary>The operator's display name shown to the user.</summary>
    [JsonPropertyName("messageFrom")] public string? MessageFrom { get; set; }
    /// <summary>The message body for the "text" kind.</summary>
    [JsonPropertyName("messageText")] public string? MessageText { get; set; }

    // For "power" commands (Commands tab). Canonicalized only for the power command type, so existing
    // command signatures stay unchanged. The agent maps the keyword to a fixed action (no shell string
    // travels the wire): "restart" | "force-restart" | "cancel" | "logout".
    [JsonPropertyName("powerAction")] public string? PowerAction { get; set; }
}

/// <summary>Known command types. Arbitrary strings are ignored.</summary>
public static class CommandTypes
{
    public const string OpenTunnel = "open-tunnel";
    public const string CloseTunnel = "close-tunnel";
    public const string Update = "update";
    public const string Ping = "ping";
    /// <summary>Show a WTS prompt to the signed-in user: availability question or a plain message.</summary>
    public const string Message = "message";
    /// <summary>Power action on the device: restart / force-restart / cancel / logout (see CommandData.PowerAction).</summary>
    public const string Power = "power";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AgentCommand))]
[JsonSerializable(typeof(CommandData))]
[JsonSerializable(typeof(TelemetryPayload))]
[JsonSerializable(typeof(Enrollment.EnrollRequest))]
[JsonSerializable(typeof(Enrollment.EnrollResponse))]
[JsonSerializable(typeof(Enrollment.EnrollError))]
[JsonSerializable(typeof(Enrollment.VncSecretReport))]
[JsonSerializable(typeof(Enrollment.BootstrapBlob))]
[JsonSerializable(typeof(Admin.DeviceInfo))]
[JsonSerializable(typeof(Admin.DeviceUpdate))]
[JsonSerializable(typeof(Admin.UpdateRequest))]
[JsonSerializable(typeof(Admin.OpenTunnelResult))]
[JsonSerializable(typeof(Admin.GroupInfo))]
[JsonSerializable(typeof(Admin.ChannelPackageInfo))]
[JsonSerializable(typeof(Admin.LoginRequest))]
[JsonSerializable(typeof(Admin.LoginResponse))]
[JsonSerializable(typeof(Admin.AuthError))]
[JsonSerializable(typeof(Admin.ChangePasswordRequest))]
[JsonSerializable(typeof(Admin.TotpConfirmRequest))]
[JsonSerializable(typeof(Admin.MeResponse))]
[JsonSerializable(typeof(Admin.ViewerPrefsRequest))]
[JsonSerializable(typeof(Admin.UserInfo))]
[JsonSerializable(typeof(Admin.CreateUserRequest))]
[JsonSerializable(typeof(Admin.CreateUserResponse))]
[JsonSerializable(typeof(Admin.UserUpdate))]
[JsonSerializable(typeof(Admin.GrantInfo))]
[JsonSerializable(typeof(Admin.GrantRequest))]
[JsonSerializable(typeof(Admin.BootstrapTokenInfo))]
[JsonSerializable(typeof(Admin.HelloRegisterRequest))]
[JsonSerializable(typeof(Admin.HelloRegisterResponse))]
[JsonSerializable(typeof(Admin.HelloCredentialInfo))]
[JsonSerializable(typeof(Admin.HelloChallengeRequest))]
[JsonSerializable(typeof(Admin.HelloChallengeResponse))]
[JsonSerializable(typeof(Admin.HelloLoginRequest))]
[JsonSerializable(typeof(Admin.StatusReport))]
[JsonSerializable(typeof(Admin.EditTokenRequest))]
[JsonSerializable(typeof(AgentUplinkMessage))]
[JsonSerializable(typeof(Admin.AccessResultInfo))]
[JsonSerializable(typeof(Admin.AuditEntryInfo))]
[JsonSerializable(typeof(System.Collections.Generic.List<Admin.AuditEntryInfo>))]
[JsonSerializable(typeof(System.Collections.Generic.List<Admin.HelloCredentialInfo>))]
[JsonSerializable(typeof(System.Collections.Generic.List<Admin.UserInfo>))]
[JsonSerializable(typeof(System.Collections.Generic.List<Admin.GrantInfo>))]
[JsonSerializable(typeof(System.Collections.Generic.List<Admin.DeviceInfo>))]
[JsonSerializable(typeof(System.Collections.Generic.List<Admin.GroupInfo>))]
[JsonSerializable(typeof(System.Collections.Generic.List<Admin.ChannelPackageInfo>))]
[JsonSerializable(typeof(System.Collections.Generic.List<Admin.BootstrapTokenInfo>))]
[JsonSerializable(typeof(Admin.ServerSettingsInfo))]
[JsonSerializable(typeof(Admin.TestEmailRequest))]
[JsonSerializable(typeof(Admin.BrandingInfo))]
[JsonSerializable(typeof(Admin.PasswordCodeRequest))]
[JsonSerializable(typeof(Admin.PasswordResetRequest))]
public sealed partial class AgentJsonContext : JsonSerializerContext;
