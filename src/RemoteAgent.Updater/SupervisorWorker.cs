using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RemoteAgent.Updater;

/// <summary>
/// Helper / supervisor a fő agent fölött. Két feladata:
///
///  1) UPDATE: ha az agent kirakott egy MÁR ELLENŐRZÖTT új exét + egy update.ready
///     markert (benne a célpath), leállítja a RemoteAgent service-t, lecseréli az
///     exét, és újraindít. Futó service nem cserélheti a SAJÁT binárisát — ezért
///     végzi ezt külön exe/service.
///
///  2) WATCHDOG: figyeli az agent életjelét (&lt;ProgramData&gt;\RemoteAgent\agent.heartbeat).
///     - ha a service NEM fut → megpróbálja elindítani;
///     - ha "fut", de az életjel elöregedett → az agent BERAGADT (az SCM ezt nem
///       látja, csak a kilépést): stop → ha időben nem áll le, a processz kilövése
///       PID alapján → restart.
///     Backoff + circuit breaker: hibás állapotban NEM loopol, hanem parkol; a
///     reboot a természetes "tiszta lap" (akkor friss próbálkozás).
///
/// A Helpernek NINCS hálózati/parancs-jogosultsága — kizárólag lokális jelekre
/// reagál (markerek + életjel). A szerverrel csak a hitelesített Agent beszél. Az
/// incidenseket egy lokális status-fájlba írja, amit az Agent visz fel telemetriával.
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
    private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(60);   // indítás után ne ítéljünk beragadást
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);  // ennyit várunk a graceful stopra, utána kill
    private const int MaxConsecutiveFailures = 5;
    private static readonly TimeSpan ParkDuration = TimeSpan.FromMinutes(10);

    // Indulástól számít a türelmi ablak: bootkor az agentnek idő kell az első életjelig,
    // ne ítéljük azonnal "beragadtnak".
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
                logger.LogWarning(ex, "Supervisor ciklus hiba.");
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
            return; // nincs mit őrizni

        if (state == ServiceState.Running)
        {
            // Indítás/csere utáni türelmi ablakban ne ítéljünk beragadást
            // (a friss agentnek idő kell az első életjelig).
            if (DateTimeOffset.UtcNow - _lastAgentAction < StartGrace)
                return;

            var age = HeartbeatAge();
            if (age <= HeartbeatStale)
            {
                // EGÉSZSÉGES: fut ÉS ver a szíve → csak ez ad "tiszta lapot".
                _consecutiveFailures = 0;
                _parkedUntil = DateTimeOffset.MinValue;
                return;
            }

            // Fut, de néma → beragadt. Parkolva ne hammerünk.
            if (DateTimeOffset.UtcNow < _parkedUntil)
                return;

            _lastIncident = $"agent beragadt (életjel ~{age.TotalSeconds:F0}s régi) — kényszerített újraindítás";
            logger.LogWarning("{Incident}", _lastIncident);
            await RestartHungAgentAsync(ct);
            await RegisterFailureAsync(); // a beragadás-churn is törje meg a breakert
            return;
        }

        // Nem fut: indítás backoff + circuit breaker mellett.
        if (DateTimeOffset.UtcNow < _parkedUntil)
            return; // parkolva — nem loopolunk

        logger.LogInformation("A RemoteAgent nem fut ({State}) — indítás.", state);
        if (await StartAsync(AgentService))
        {
            _lastAgentAction = DateTimeOffset.UtcNow;
            _agentRestarts++;
            _lastIncident = "agent leállt → újraindítva";
            // a "tiszta lapot" NEM itt adjuk: csak ha a következő körben egészséges (fut + életjel).
            await WriteStatusAsync();
        }
        else
        {
            _lastIncident = "agent indítása sikertelen";
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

    /// <summary>Sikertelen helyreállítás könyvelése; küszöb felett parkolás (nincs loop).</summary>
    private async Task RegisterFailureAsync()
    {
        _consecutiveFailures++;
        if (_consecutiveFailures >= MaxConsecutiveFailures)
        {
            _parkedUntil = DateTimeOffset.UtcNow + ParkDuration;
            logger.LogError("Túl sok sikertelen helyreállítás ({N}) — parkolás {Min} percre (nincs loop). Reboot után friss próbálkozás.",
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
            logger.LogWarning("Üres update.ready (nincs célpath), eldobva.");
            TryDelete(marker);
            return;
        }

        logger.LogInformation("Update észlelve → {Target} cseréje.", target);

        await StopWithKillAsync(AgentService, ct);

        bool copied = false;
        for (int i = 0; i < 10 && !copied; i++)
        {
            try { File.Copy(newExe, target, overwrite: true); copied = true; }
            catch (IOException) { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
        }

        if (!copied)
        {
            logger.LogError("Az exe cseréje nem sikerült (zárolt?). A service-t újraindítom a régivel.");
            await StartAsync(AgentService);
            _lastAgentAction = DateTimeOffset.UtcNow;
            return;
        }

        TryDelete(marker);
        TryDelete(newExe);
        await StartAsync(AgentService);
        _lastAgentAction = DateTimeOffset.UtcNow;
        _lastIncident = "agent frissítve (exe-csere)";
        await WriteStatusAsync();
        logger.LogInformation("Update alkalmazva, a RemoteAgent újraindítva.");
    }

    // ---------------- SERVICE OPS (sc.exe) ----------------

    private enum ServiceState { NotInstalled, Stopped, Running, Other }

    private static async Task<ServiceState> QueryStateAsync(string name)
    {
        var (code, output) = await RunCaptureAsync("sc.exe", "query", name);
        if (code != 0) return ServiceState.NotInstalled; // 1060 = nincs ilyen service
        if (output.Contains("RUNNING")) return ServiceState.Running;
        if (output.Contains("STOPPED")) return ServiceState.Stopped;
        return ServiceState.Other; // START_PENDING / STOP_PENDING stb.
    }

    private async Task<bool> StartAsync(string name)
    {
        await RunAsync("sc.exe", "start", name);
        for (int i = 0; i < 15; i++) // ~15s, amíg tényleg fut
        {
            if (await QueryStateAsync(name) == ServiceState.Running) return true;
            await Task.Delay(1000);
        }
        return await QueryStateAsync(name) == ServiceState.Running;
    }

    /// <summary>Graceful stop; ha az időkereten belül nem áll le → a processz kilövése PID alapján.</summary>
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
            logger.LogWarning("A(z) {Service} nem állt le {Sec}s alatt — processz kilövése (PID {Pid}).",
                name, StopTimeout.TotalSeconds, pid);
            try { Process.GetProcessById(pid.Value).Kill(entireProcessTree: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Kill sikertelen."); }
        }

        for (int i = 0; i < 10; i++) // adjunk az SCM-nek időt a 'stopped'-re
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
