using System.Text.Json.Serialization;

namespace RemoteAgent.Admin;

/// <summary>
/// Egy komponens (agent/helper/client) LOKÁLIS, csak-olvasható állapot-riportja, amit a saját
/// named pipe-ján ad ki (pl. "RemoteAgent.status"). Csak állapot — SEMMI titok és SEMMI parancs.
/// A séma verziózott, mert vegyes verziójú flottán fognak egymással beszélni.
/// </summary>
public sealed class StatusReport
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;

    /// <summary>"agent" | "helper" | "client"</summary>
    [JsonPropertyName("component")] public string Component { get; set; } = string.Empty;
    /// <summary>Az agent (a riportot adó komponens) verziója.</summary>
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;

    /// <summary>A gépen lévő többi komponens verziója (a Névjegyhez). Null = nincs/ismeretlen.</summary>
    [JsonPropertyName("helperVersion")] public string? HelperVersion { get; set; }
    [JsonPropertyName("clientVersion")] public string? ClientVersion { get; set; }
    [JsonPropertyName("vncVersion")] public string? VncVersion { get; set; }

    /// <summary>Összesített „jó-e a környezet" jelzés (a komponens dönti el, mi alapján).</summary>
    [JsonPropertyName("healthy")] public bool Healthy { get; set; }

    /// <summary>Él-e a szerver felé a C2 (WSS) parancscsatorna.</summary>
    [JsonPropertyName("c2Connected")] public bool C2Connected { get; set; }

    /// <summary>Él-e a reverse tunnel (a szerver felőli eléréshez).</summary>
    [JsonPropertyName("tunnelActive")] public bool TunnelActive { get; set; }

    /// <summary>Utolsó sikeres szerver-kontakt (C2 csatlakozás vagy telemetria) ideje.</summary>
    [JsonPropertyName("lastServerContactUtc")] public DateTimeOffset? LastServerContactUtc { get; set; }

    /// <summary>A helyi agent gépazonosítója (a kliens ezt küldi a login/reset kérésben a gép-szintű fail-counterhez).</summary>
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
}
