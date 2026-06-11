using System.Reflection;
using RemoteAgent.Configuration;
using RemoteAgent.Tunnel;
using Microsoft.Extensions.Options;

namespace RemoteAgent.Telemetry;

/// <summary>
/// Összeállítja a telemetria payloadot. Szándékosan csak BCL-API-kat használ
/// (nincs WMI/System.Management), hogy NativeAOT alatt is gond nélkül fusson.
/// Mélyebb leltárhoz (CPU, RAM, lemez) ide jöhet CIM/registry lekérdezés.
/// </summary>
public sealed class SystemInfoCollector(IOptions<AgentOptions> options, TunnelState tunnelState)
{
    private readonly AgentOptions _options = options.Value;

    public TelemetryPayload Collect() => new()
    {
        AgentId = MachineIdentity.Resolve(_options.AgentId),
        Hostname = Environment.MachineName,
        OsVersion = Environment.OSVersion.VersionString,
        AgentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
        BootTimeUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64),
        CollectedAtUtc = DateTimeOffset.UtcNow,
        TunnelActive = tunnelState.IsActive,
    };
}

/// <summary>Stabil gépazonosító feloldása.</summary>
public static class MachineIdentity
{
    public static string Resolve(string configured) =>
        string.IsNullOrWhiteSpace(configured) ? Environment.MachineName : configured;
}
