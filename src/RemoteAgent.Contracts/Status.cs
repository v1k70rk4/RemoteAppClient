using System.Text.Json.Serialization;

namespace RemoteAgent.Admin;

/// <summary>
/// Local, read-only status report exposed by a component (agent/helper/client) on its own
/// named pipe, for example "RemoteAgent.status". State only: no secrets and no commands.
/// The schema is versioned because mixed-version fleets will communicate with it.
/// </summary>
public sealed class StatusReport
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;

    /// <summary>"agent" | "helper" | "client"</summary>
    [JsonPropertyName("component")] public string Component { get; set; } = string.Empty;
    /// <summary>Version of the component that produced the report.</summary>
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;

    /// <summary>Versions of the other components on the device for the About view. Null = absent or unknown.</summary>
    [JsonPropertyName("helperVersion")] public string? HelperVersion { get; set; }
    [JsonPropertyName("clientVersion")] public string? ClientVersion { get; set; }
    [JsonPropertyName("vncVersion")] public string? VncVersion { get; set; }

    /// <summary>Overall health signal, interpreted by the reporting component.</summary>
    [JsonPropertyName("healthy")] public bool Healthy { get; set; }

    /// <summary>Whether the C2 command channel (WSS) to the server is connected.</summary>
    [JsonPropertyName("c2Connected")] public bool C2Connected { get; set; }

    /// <summary>Whether the reverse tunnel used for server-side access is active.</summary>
    [JsonPropertyName("tunnelActive")] public bool TunnelActive { get; set; }

    /// <summary>Configured bastion transport: "auto" | "ssl443" | "ssh22" | "wss443". Null = unknown/auto.</summary>
    [JsonPropertyName("bastionTransport")] public string? BastionTransport { get; set; }

    /// <summary>Bastion port the last tunnel actually connected on (443 or 22), shown in About. 0 = none yet.</summary>
    [JsonPropertyName("activeBastionPort")] public int ActiveBastionPort { get; set; }

    /// <summary>Time of the last successful server contact, either C2 connection or telemetry.</summary>
    [JsonPropertyName("lastServerContactUtc")] public DateTimeOffset? LastServerContactUtc { get; set; }

    /// <summary>Agent liveness tick, updated by the agent roughly every 15 s. The Helper reads it over this
    /// status pipe to detect a hung agent (stale or missing tick), replacing the old heartbeat file.</summary>
    [JsonPropertyName("lastHeartbeatUtc")] public DateTimeOffset? LastHeartbeatUtc { get; set; }

    /// <summary>Local agent device ID sent by the client in login/reset requests for the device-level failure counter.</summary>
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
}
