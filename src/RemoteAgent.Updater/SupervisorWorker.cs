using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using L = RemoteAgent.Updater.Localization.Strings;

namespace RemoteAgent.Updater;

/// <summary>
/// Helper / supervisor that watches the main agent. It has two jobs:
///
///  1) UPDATE: when the agent stages an already verified new executable plus an
///     update.ready marker containing the target path, it stops the RemoteAgent
///     service, replaces the executable, and restarts it. A running service cannot
///     replace its own binary, so this lives in a separate executable/service.
///
///  2) WATCHDOG: watches the agent heartbeat file
///     (&lt;ProgramData&gt;\RemoteAgent\agent.heartbeat).
///     - if the service is not running, it tries to start it;
///     - if the service appears running but the heartbeat is stale, the agent is hung
///       (SCM only sees process exit): stop, kill by PID if it does not stop in time,
///       then restart.
///     Backoff and circuit breaker prevent a tight failure loop; reboot is the natural reset.
///
/// The Helper has no network or command authority. It only reacts to local markers and
/// heartbeat files. Only the authenticated Agent talks to the server. Incidents are
/// written to a local status file and uploaded by the Agent as telemetry.
/// </summary>
public sealed class SupervisorWorker(ILogger<SupervisorWorker> logger) : BackgroundService
{
    private const string AgentService = "RemoteAgent";

    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteAgent");
    private static readonly string UpdateDir = Path.Combine(DataDir, "update");
    private static readonly string HeartbeatFile = Path.Combine(DataDir, "agent.heartbeat");
    private static readonly string StatusFile = Path.Combine(DataDir, "supervisor.status");

    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatStale = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(60);   // do not judge hang immediately after start
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);  // graceful stop window before killing
    private const int MaxConsecutiveFailures = 5;
    private static readonly TimeSpan ParkDuration = TimeSpan.FromMinutes(10);

    // Grace window starts at process start: after boot the agent needs time to emit
    // its first heartbeat, so do not classify it as hung immediately.
    private DateTimeOffset _lastAgentAction = DateTimeOffset.UtcNow;
    private DateTimeOffset _parkedUntil = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private int _agentRestarts;
    private string? _lastIncident;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var marker = Path.Combine(UpdateDir, "update.ready");
        var newExe = Path.Combine(UpdateDir, "RemoteAgent.exe");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(marker) && File.Exists(newExe))
                    await SwapAgentAsync(marker, newExe, stoppingToken);
                else
                    await WatchdogAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, L.SupervisorWorker_012);
            }

            try { await Task.Delay(Poll, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ---------------- WATCHDOG ----------------

    private async Task WatchdogAsync(CancellationToken ct)
    {
        var state = await QueryStateAsync(AgentService);
        if (state == ServiceState.NotInstalled)
            return; // nothing to supervise

        if (state == ServiceState.Running)
        {
            // During the post-start/swap grace window, the fresh agent needs time
            // to emit its first heartbeat.
            if (DateTimeOffset.UtcNow - _lastAgentAction < StartGrace)
                return;

            var age = HeartbeatAge();
            if (age <= HeartbeatStale)
            {
                // Healthy means both running and heartbeat present; only this resets failure state.
                _consecutiveFailures = 0;
                _parkedUntil = DateTimeOffset.MinValue;
                return;
            }

            // Running but silent means hung. When parked, do not hammer SCM.
            if (DateTimeOffset.UtcNow < _parkedUntil)
                return;

            _lastIncident = L.Format(L.SupervisorWorker_001, age.TotalSeconds);
            logger.LogWarning("{Incident}", _lastIncident);
            await RestartHungAgentAsync(ct);
            await RegisterFailureAsync(); // hung-service churn should also trip the breaker
            return;
        }

        // Not running: start it with backoff and circuit breaker protection.
        if (DateTimeOffset.UtcNow < _parkedUntil)
            return; // parked; do not loop

        logger.LogInformation(L.SupervisorWorker_002, state);
        if (await StartAsync(AgentService))
        {
            _lastAgentAction = DateTimeOffset.UtcNow;
            _agentRestarts++;
            _lastIncident = L.SupervisorWorker_003;
            // Do not reset here; only the next healthy cycle (running + heartbeat) clears failures.
            await WriteStatusAsync();
        }
        else
        {
            _lastIncident = L.SupervisorWorker_004;
            logger.LogError("{Incident}", _lastIncident);
            await RegisterFailureAsync();
        }
    }

    private async Task RestartHungAgentAsync(CancellationToken ct)
    {
        await StopWithKillAsync(AgentService, ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await StartAsync(AgentService);
        _lastAgentAction = DateTimeOffset.UtcNow;
        _agentRestarts++;
    }

    /// <summary>Records a failed recovery and parks above the threshold to avoid loops.</summary>
    private async Task RegisterFailureAsync()
    {
        _consecutiveFailures++;
        if (_consecutiveFailures >= MaxConsecutiveFailures)
        {
            _parkedUntil = DateTimeOffset.UtcNow + ParkDuration;
            logger.LogError(L.SupervisorWorker_005,
                _consecutiveFailures, ParkDuration.TotalMinutes);
        }
        await WriteStatusAsync();
    }

    private static TimeSpan HeartbeatAge()
    {
        try
        {
            if (!File.Exists(HeartbeatFile)) return TimeSpan.MaxValue;
            var txt = File.ReadAllText(HeartbeatFile).Trim();
            if (DateTimeOffset.TryParse(txt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts))
                return DateTimeOffset.UtcNow - ts;
            return DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(HeartbeatFile); // fallback
        }
        catch { return TimeSpan.MaxValue; }
    }

    // ---------------- UPDATE SWAP ----------------

    private async Task SwapAgentAsync(string marker, string newExe, CancellationToken ct)
    {
        var target = (await File.ReadAllTextAsync(marker, ct)).Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            logger.LogWarning(L.SupervisorWorker_006);
            TryDelete(marker);
            return;
        }

        logger.LogInformation(L.SupervisorWorker_007, target);

        await StopWithKillAsync(AgentService, ct);

        bool copied = false;
        for (int i = 0; i < 10 && !copied; i++)
        {
            try { File.Copy(newExe, target, overwrite: true); copied = true; }
            catch (IOException) { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
        }

        if (!copied)
        {
            logger.LogError(L.SupervisorWorker_008);
            await StartAsync(AgentService);
            _lastAgentAction = DateTimeOffset.UtcNow;
            return;
        }

        TryDelete(marker);
        TryDelete(newExe);
        await StartAsync(AgentService);
        _lastAgentAction = DateTimeOffset.UtcNow;
        _lastIncident = L.SupervisorWorker_009;
        await WriteStatusAsync();
        logger.LogInformation(L.SupervisorWorker_010);
    }

    // ---------------- SERVICE OPS (sc.exe) ----------------

    private enum ServiceState { NotInstalled, Stopped, Running, Other }

    private static async Task<ServiceState> QueryStateAsync(string name)
    {
        var (code, output) = await RunCaptureAsync("sc.exe", "query", name);
        if (code != 0) return ServiceState.NotInstalled; // 1060 = no such service
        if (output.Contains("RUNNING")) return ServiceState.Running;
        if (output.Contains("STOPPED")) return ServiceState.Stopped;
        return ServiceState.Other; // START_PENDING / STOP_PENDING stb.
    }

    private async Task<bool> StartAsync(string name)
    {
        await RunAsync("sc.exe", "start", name);
        for (int i = 0; i < 15; i++) // about 15s until it is actually running
        {
            if (await QueryStateAsync(name) == ServiceState.Running) return true;
            await Task.Delay(1000);
        }
        return await QueryStateAsync(name) == ServiceState.Running;
    }

    /// <summary>Graceful stop; if the timeout elapses, kills the process by PID.</summary>
    private async Task StopWithKillAsync(string name, CancellationToken ct)
    {
        await RunAsync("sc.exe", "stop", name);

        var deadline = DateTimeOffset.UtcNow + StopTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await QueryStateAsync(name) == ServiceState.Stopped) return;
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
        }

        var pid = await QueryPidAsync(name);
        if (pid is > 0)
        {
            logger.LogWarning(L.SupervisorWorker_011,
                name, StopTimeout.TotalSeconds, pid);
            try { Process.GetProcessById(pid.Value).Kill(entireProcessTree: true); }
            catch (Exception ex) { logger.LogWarning(ex, L.SupervisorWorker_013); }
        }

        for (int i = 0; i < 10; i++) // give SCM time to report 'stopped'
        {
            if (await QueryStateAsync(name) == ServiceState.Stopped) return;
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private static async Task<int?> QueryPidAsync(string name)
    {
        var (code, output) = await RunCaptureAsync("sc.exe", "queryex", name);
        if (code != 0) return null;
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("PID", StringComparison.OrdinalIgnoreCase))
            {
                var parts = t.Split(':', 2);
                if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out var pid)) return pid;
            }
        }
        return null;
    }

    private async Task WriteStatusAsync()
    {
        try
        {
            var status = new SupervisorStatus
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                AgentRestarts = _agentRestarts,
                ConsecutiveFailures = _consecutiveFailures,
                Parked = DateTimeOffset.UtcNow < _parkedUntil,
                LastIncident = _lastIncident,
            };
            Directory.CreateDirectory(DataDir);
            await File.WriteAllTextAsync(StatusFile, JsonSerializer.Serialize(status, SupervisorJson.Default.SupervisorStatus));
        }
        catch { /* best effort */ }
    }

    private static async Task RunAsync(string file, params string[] args)
    {
        try { await RunCaptureAsync(file, args); } catch { /* best effort */ }
    }

    private static async Task<(int code, string output)> RunCaptureAsync(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
