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
}

/// <summary>Ismert parancstípusok. Tetszőleges stringet nem dolgozunk fel.</summary>
public static class CommandTypes
{
    public const string OpenTunnel = "open-tunnel";
    public const string CloseTunnel = "close-tunnel";
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
[JsonSerializable(typeof(Admin.DeviceInfo))]
[JsonSerializable(typeof(Admin.DeviceUpdate))]
[JsonSerializable(typeof(Admin.OpenTunnelResult))]
[JsonSerializable(typeof(Admin.GroupInfo))]
[JsonSerializable(typeof(System.Collections.Generic.List<Admin.DeviceInfo>))]
[JsonSerializable(typeof(System.Collections.Generic.List<Admin.GroupInfo>))]
public sealed partial class AgentJsonContext : JsonSerializerContext;
