using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using RemoteAgent.Configuration;
using RemoteAgent.Tunnel;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace RemoteAgent.Telemetry;

/// <summary>
/// Összeállítja a telemetria payloadot. Szándékosan csak BCL-API-kat használ
/// (nincs WMI/System.Management), hogy NativeAOT alatt is gond nélkül fusson.
/// A komponens-verziókat a gépen lévő binárisok fájlverziójából olvassa, a
/// supervisor állapotát a Helper által írt supervisor.status fájlból.
/// </summary>
public sealed class SystemInfoCollector(IOptions<AgentOptions> options, TunnelState tunnelState)
{
    private readonly AgentOptions _options = options.Value;

    public TelemetryPayload Collect()
    {
        var p = new TelemetryPayload
        {
            AgentId = MachineIdentity.Resolve(_options.AgentId),
            Hostname = Environment.MachineName,
            OsVersion = Environment.OSVersion.VersionString,
            AgentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            HelperVersion = FileVersion(CoLocated("RemoteAgent.Updater.exe")),
            ClientVersion = FileVersion(CoLocated("RemoteClient.exe")),
            VncVersion = VncVersion(),
            BootTimeUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64),
            CollectedAtUtc = DateTimeOffset.UtcNow,
            TunnelActive = tunnelState.IsActive,
            VncLocked = Vnc.VncLock.IsLocked(),
        };
        ReadSupervisorStatus(p);
        return p;
    }

    /// <summary>Egy az agent exe mellé telepített fájl teljes útja, ha létezik.</summary>
    private static string? CoLocated(string name)
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var path = Path.Combine(dir, name);
        return File.Exists(path) ? path : null;
    }

    private static string? FileVersion(string? path)
    {
        if (path is null) return null;
        try
        {
            // A számtagokból építjük: a natív exék (pl. TightVNC) nyers FileVersion-je
            // vesszős lehet ("2, 8, 87, 0") — így mindig pontozott formát kapunk.
            var fvi = FileVersionInfo.GetVersionInfo(path);
            return $"{fvi.FileMajorPart}.{fvi.FileMinorPart}.{fvi.FileBuildPart}.{fvi.FilePrivatePart}";
        }
        catch { return null; }
    }

    /// <summary>A TightVNC verziója: a tvnserver service ImagePath-jából a tvnserver.exe fájlverziója.</summary>
    private static string? VncVersion()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\tvnserver");
            if (k?.GetValue("ImagePath") is not string img || string.IsNullOrWhiteSpace(img)) return null;

            // Az ImagePath idézőjeles és/vagy argumentumos lehet: "...\tvnserver.exe" -service
            string path = img.Trim();
            if (path.StartsWith('"'))
            {
                int end = path.IndexOf('"', 1);
                if (end > 1) path = path[1..end];
            }
            else
            {
                int exe = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exe > 0) path = path[..(exe + 4)];
            }
            return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null;
        }
        catch { return null; }
    }

    /// <summary>A Helper supervisor.status JSON-jából az agent-újraindítások + utolsó incidens.</summary>
    private void ReadSupervisorStatus(TelemetryPayload p)
    {
        try
        {
            var path = Path.Combine(_options.EnrollmentDir, "supervisor.status");
            if (!File.Exists(path)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("agentRestarts", out var ar) && ar.TryGetInt32(out var n))
                p.AgentRestarts = n;
            if (root.TryGetProperty("lastIncident", out var li) && li.ValueKind == JsonValueKind.String)
                p.LastIncident = li.GetString();
        }
        catch { /* best effort */ }
    }
}

/// <summary>Stabil gépazonosító feloldása.</summary>
public static class MachineIdentity
{
    public static string Resolve(string configured) =>
        string.IsNullOrWhiteSpace(configured) ? Environment.MachineName : configured;
}
