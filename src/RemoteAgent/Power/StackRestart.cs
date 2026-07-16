using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace RemoteAgent.Power;

/// <summary>
/// Restarts this device's RemoteAppClient stack: VNC, then the Helper, then the agent itself.
///
/// The order is not cosmetic. The agent cannot resurrect itself — it does not even self-restart on
/// update: it stages the new exe and the Helper performs the swap — so the Helper is the one component
/// able to bring a dead agent back. Hence:
///
///   1. tvnserver — independent of the control plane. A failure here is not worth aborting for, and
///                  nothing critical has been touched yet.
///   2. Helper    — restart, then VERIFY it is running again. If it does not come back we ABORT and
///                  leave the agent running: an agent down with no supervisor strands the device with
///                  no C2 until somebody reboots it by hand.
///   3. agent     — last, and only after the caller has reported the outcome (the agent dies with it).
///                  Handed to a detached restarter so recovery does not hinge on the Helper's poll
///                  timing; the just-verified Helper is the backstop if that restarter dies.
/// </summary>
public static class StackRestart
{
    private const string VncService = "tvnserver";
    private const string HelperService = "RemoteAgent.Updater";
    private const string AgentService = "RemoteAgent";

    /// <summary>
    /// Runs steps 1-2. True means the Helper is verified alive and <see cref="ScheduleAgentRestart"/> may
    /// run; false means we aborted and the agent is still up (report "failed" and change nothing).
    /// </summary>
    public static async Task<bool> PrepareAsync(ILogger logger, CancellationToken ct)
    {
        if (await RestartServiceAsync(VncService, ct))
            logger.LogInformation("Stack restart: {Service} restarted.", VncService);
        else
            logger.LogWarning("Stack restart: {Service} did not come back; continuing (VNC is not on the control path).", VncService);

        if (!await RestartServiceAsync(HelperService, ct))
        {
            logger.LogError(
                "Stack restart ABORTED: {Service} is not running after the restart, so nothing could revive a stopped agent. Leaving the agent up.",
                HelperService);
            return false;
        }

        logger.LogInformation("Stack restart: {Service} restarted and verified running.", HelperService);
        return true;
    }

    /// <summary>
    /// Hands the agent's own restart to a detached cmd that outlives this process. Call ONLY after the
    /// command outcome has been reported — the agent stops a few seconds later. "net stop" blocks until
    /// the service is actually stopped, so the following "net start" cannot race it.
    /// </summary>
    public static void ScheduleAgentRestart(ILogger logger)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c timeout /t 5 /nobreak >nul & net stop {AgentService} & net start {AgentService}",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
            logger.LogInformation("Stack restart: agent restart handed to a detached restarter.");
        }
        catch (Exception ex)
        {
            // Not fatal: the Helper was verified alive above and restarts a dead agent on its own.
            logger.LogError(ex, "Stack restart: could not spawn the agent restarter; the Helper should recover the agent.");
        }
    }

    /// <summary>Stops and starts a service, returning whether it is running again afterwards.</summary>
    private static async Task<bool> RestartServiceAsync(string name, CancellationToken ct)
    {
        if (!await ExistsAsync(name, ct)) return false;

        await RunAsync("sc.exe", $"stop {name}", ct);                       // may already be stopped
        await WaitForStateAsync(name, "STOPPED", TimeSpan.FromSeconds(20), ct);
        await RunAsync("sc.exe", $"start {name}", ct);
        return await WaitForStateAsync(name, "RUNNING", TimeSpan.FromSeconds(30), ct);
    }

    private static async Task<bool> ExistsAsync(string name, CancellationToken ct) =>
        (await RunCaptureAsync("sc.exe", $"query {name}", ct)).Code == 0;

    private static async Task<bool> WaitForStateAsync(string name, string state, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var (code, output) = await RunCaptureAsync("sc.exe", $"query {name}", ct);
            if (code == 0 && output.Contains(state, StringComparison.OrdinalIgnoreCase)) return true;
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private static async Task RunAsync(string file, string args, CancellationToken ct) =>
        await RunCaptureAsync(file, args, ct);

    private static async Task<(int Code, string Output)> RunCaptureAsync(string file, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return (p.ExitCode, output);
        }
        catch { return (-1, ""); }
    }
}
