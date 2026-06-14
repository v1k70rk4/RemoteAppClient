using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Configuration;
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
                logger.LogWarning(ex, L.HelperUpdateWatcher_005);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SwapAsync(string marker, string newExe, CancellationToken ct)
    {
        var target = (await File.ReadAllTextAsync(marker, ct)).Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            logger.LogWarning(L.HelperUpdateWatcher_001);
            TryDelete(marker);
            return;
        }

        logger.LogInformation(L.HelperUpdateWatcher_002, target);

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
            logger.LogError(L.HelperUpdateWatcher_003);
            await RunNetAsync("start", UpdaterService);
            return;
        }

        TryDelete(marker);
        TryDelete(newExe);
        await RunNetAsync("start", UpdaterService);
        logger.LogInformation(L.HelperUpdateWatcher_004);
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
