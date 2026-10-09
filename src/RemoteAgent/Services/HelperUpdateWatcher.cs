using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Configuration;
using RemoteAgent.Security;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Services;

/// <summary>
/// Solves the "who updates the updater" problem. A running service cannot replace its
/// own binary, so the Helper replaces the agent executable, while this watcher inside
/// the Agent replaces the Helper executable.
/// When UpdateInstaller stages a verified new Updater executable plus update.updater.ready
/// containing the Helper target path, this stops RemoteAgent.Updater, replaces it, and restarts it.
/// </summary>
public sealed class HelperUpdateWatcher(IOptions<AgentOptions> options, ILogger<HelperUpdateWatcher> logger) : BackgroundService
{
    private const string UpdaterService = "RemoteAgent.Updater";
    private readonly string _dir = Path.Combine(options.Value.EnrollmentDir, "update");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var marker = Path.Combine(_dir, "update.updater.ready");
        var newExe = Path.Combine(_dir, "RemoteAgent.Updater.exe");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(marker) && File.Exists(newExe))
                    await SwapAsync(marker, newExe, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, L.HelperUpdateWatcher_HelperReplacementFailed);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SwapAsync(string marker, string newExe, CancellationToken ct)
    {
        // Only this agent (SYSTEM) or an administrator stages an update; anything else is not acted on.
        if (!DataDirectorySecurity.IsOwnedBySystemOrAdministrators(marker) ||
            !DataDirectorySecurity.IsOwnedBySystemOrAdministrators(newExe))
        {
            logger.LogWarning(L.HelperUpdateWatcher_StagingNotBySystemIgnored);
            TryDelete(marker);
            TryDelete(newExe);
            return;
        }

        // The path replaced is the Helper service's own executable, from the registry - never one from a file.
        if ((await File.ReadAllTextAsync(marker, ct)).Trim().Length == 0)
        {
            logger.LogWarning(L.HelperUpdateWatcher_EmptyUpdateUpdaterReadyNo);
            TryDelete(marker);
            return;
        }
        var target = DataDirectorySecurity.ServiceExecutablePath(UpdaterService);
        if (target is null)
        {
            logger.LogWarning(L.HelperUpdateWatcher_NoServicePath);
            TryDelete(marker);
            return;
        }

        logger.LogInformation(L.HelperUpdateWatcher_HelperUpdateDetectedReplacingTarget, target);

        await RunNetAsync("stop", UpdaterService);
        await Task.Delay(TimeSpan.FromSeconds(2), ct); // az exe felszabaduljon

        bool copied = false;
        for (int i = 0; i < 10 && !copied; i++)
        {
            try { File.Copy(newExe, target, overwrite: true); copied = true; }
            catch (IOException) { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
        }

        if (!copied)
        {
            logger.LogError(L.HelperUpdateWatcher_CouldNotReplaceTheHelper);
            await RunNetAsync("start", UpdaterService);
            return;
        }

        TryDelete(marker);
        TryDelete(newExe);
        await RunNetAsync("start", UpdaterService);
        logger.LogInformation(L.HelperUpdateWatcher_HelperUpdatedRemoteAgentUpdaterRestarted);
    }

    private static async Task RunNetAsync(string verb, string service)
    {
        try
        {
            // 'net' is synchronous and waits for stop/start.
            using var proc = Process.Start(new ProcessStartInfo("net", $"{verb} \"{service}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            })!;
            await proc.WaitForExitAsync();
        }
        catch { /* best effort */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
