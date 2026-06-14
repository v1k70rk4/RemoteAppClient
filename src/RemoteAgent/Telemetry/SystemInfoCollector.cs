using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using RemoteAgent.Configuration;
using RemoteAgent.Tunnel;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace RemoteAgent.Telemetry;

/// <summary>
/// Builds the telemetry payload. Intentionally uses only BCL APIs (no WMI/System.Management)
/// so it remains NativeAOT-friendly. Component versions are read from file versions of
/// binaries on the device; supervisor state comes from the Helper-written supervisor.status.
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
            IpAddress = NetInfo.PrimaryIPv4(),
            WifiSsid = NetInfo.WifiSsid(),
            VpnActive = NetInfo.IsVpnActive(),
            LoggedInUser = Consent.ConsentPrompt.ActiveUserName(),
        };
        ReadSupervisorStatus(p);
        return p;
    }

    /// <summary>Versions of co-installed components (helper/client/vnc) for the local status pipe. Cheap.</summary>
    public (string? Helper, string? Client, string? Vnc) ComponentVersions() =>
        (FileVersion(CoLocated("RemoteAgent.Updater.exe")), FileVersion(CoLocated("RemoteClient.exe")), VncVersion());

    /// <summary>Full path of a file installed next to the agent executable, when it exists.</summary>
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
            // Build from numeric parts: native executables such as TightVNC can expose
            // raw FileVersion with commas ("2, 8, 87, 0"), so this always returns dotted form.
            var fvi = FileVersionInfo.GetVersionInfo(path);
            return $"{fvi.FileMajorPart}.{fvi.FileMinorPart}.{fvi.FileBuildPart}.{fvi.FilePrivatePart}";
        }
        catch { return null; }
    }

    /// <summary>TightVNC version from the tvnserver.exe file version resolved from the service ImagePath.</summary>
    private static string? VncVersion()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\tvnserver");
            if (k?.GetValue("ImagePath") is not string img || string.IsNullOrWhiteSpace(img)) return null;

            // ImagePath can be quoted and/or include arguments: "...\tvnserver.exe" -service.
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
            // FileVersion returns dotted form. TightVNC raw FileVersion can be comma-separated
            // ("2, 8, 87, 0"), but rollout skip checks compare against "2.8.87.0".
            return File.Exists(path) ? FileVersion(path) : null;
        }
        catch { return null; }
    }

    /// <summary>Reads agent restart count and last incident from the Helper supervisor.status JSON.</summary>
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

/// <summary>Resolves the stable device identifier.</summary>
public static class MachineIdentity
{
    public static string Resolve(string configured) =>
        string.IsNullOrWhiteSpace(configured) ? Environment.MachineName : configured;
}
