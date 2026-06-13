using System.Text.Json.Serialization;
using RemoteAgent.Telemetry;

namespace RemoteAgent.Commands;

/// <summary>
/// A szerverről érkező, aláírt parancs. A <see cref="Signature"/> a payload
/// kanonikus formája feletti ECDSA aláírás (lásd <see cref="CommandSignature"/>);
/// a <see cref="Nonce"/> és <see cref="IssuedAt"/> a replay-védelem.
/// KÖZÖS típus: a kliens és a szerver ugyanezt használja, így nem csúszhat szét.
/// </summary>
public sealed class AgentCommand
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Egyedi parancsazonosító (GUID), egyben nonce a replay-cache-hez.</summary>
    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;

    /// <summary>Kibocsátás ideje (Unix epoch másodperc).</summary>
    [JsonPropertyName("iat")]
    public long IssuedAt { get; set; }

    /// <summary>Parancs-specifikus adat (pl. melyik szerver-portra menjen a forward).</summary>
    [JsonPropertyName("data")]
    public CommandData? Data { get; set; }

    /// <summary>A fenti mezők kanonikus JSON-ja feletti aláírás (Base64).</summary>
    [JsonPropertyName("sig")]
    public string Signature { get; set; } = string.Empty;
}

public sealed class CommandData
{
    /// <summary>Tunnel parancsnál: melyik távoli (bástya-oldali) portot nyissa a forward.</summary>
    [JsonPropertyName("remotePort")]
    public int RemotePort { get; set; }

    // Update parancsnál: a csomag verziója, letöltési URL-je és SHA-256 hash-e.
    // Mindegyik az aláírás alá esik (lásd CommandSignature.Canonicalize).
    [JsonPropertyName("version")]
    public string? UpdateVersion { get; set; }

    [JsonPropertyName("url")]
    public string? UpdateUrl { get; set; }

    [JsonPropertyName("sha256")]
    public string? UpdateSha256 { get; set; }

    /// <summary>
    /// Melyik komponenst frissíti: "agent" (alapértelmezett) vagy "updater"/"helper".
    /// Az "updater" csomagot az AGENT cseréli (a Helper a saját futó exéjét nem tudja),
    /// az "agent" csomagot a Helper. Az aláírás alá esik.
    /// </summary>
    [JsonPropertyName("target")]
    public string? UpdateTarget { get; set; }
}

/// <summary>Ismert parancstípusok. Tetszőleges stringet nem dolgozunk fel.</summary>
public static class CommandTypes
{
    public const string OpenTunnel = "open-tunnel";
    public const string CloseTunnel = "close-tunnel";
    public const string Update = "update";
    public const string Ping = "ping";
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
[JsonSerializable(typeof(System.Collections.Generic.List<Admin.HelloCredentialInfo>))]
[JsonSerializable(typeof(System.Collections.Generic.List<Admin.UserInfo>))]
[JsonSerializable(typeof(System.Collections.Generic.List<Admin.GrantInfo>))]
[JsonSerializable(typeof(System.Collections.Generic.List<Admin.DeviceInfo>))]
[JsonSerializable(typeof(System.Collections.Generic.List<Admin.GroupInfo>))]
[JsonSerializable(typeof(System.Collections.Generic.List<Admin.ChannelPackageInfo>))]
[JsonSerializable(typeof(System.Collections.Generic.List<Admin.BootstrapTokenInfo>))]
public sealed partial class AgentJsonContext : JsonSerializerContext;
