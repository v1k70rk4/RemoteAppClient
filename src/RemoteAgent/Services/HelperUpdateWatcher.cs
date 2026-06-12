using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Configuration;

namespace RemoteAgent.Services;

/// <summary>
/// A "ki frissíti az Updatert" probléma megoldása. Futó service nem cserélheti a
/// SAJÁT binárisát, ezért:
///  - az agent exéjét a Helper (RemoteAgent.Updater) cseréli;
///  - a Helper exéjét EZ a figyelő (az Agentben) cseréli.
/// Ha az UpdateInstaller kirakott egy ellenőrzött új Updater exét + egy
/// update.updater.ready markert (benne a Helper exe célpathja): leállítja a
/// RemoteAgent.Updater service-t, lecseréli az exét, és újraindítja.
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
                logger.LogWarning(ex, "Helper-csere sikertelen.");
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
            logger.LogWarning("Üres update.updater.ready (nincs célpath), eldobva.");
            TryDelete(marker);
            return;
        }

        logger.LogInformation("Helper-update észlelve → {Target} cseréje.", target);

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
            logger.LogError("A Helper exe cseréje nem sikerült (zárolt?). Újraindítom a régivel.");
            await RunNetAsync("start", UpdaterService);
            return;
        }

        TryDelete(marker);
        TryDelete(newExe);
        await RunNetAsync("start", UpdaterService);
        logger.LogInformation("Helper frissítve, a RemoteAgent.Updater újraindítva.");
    }

    private static async Task RunNetAsync(string verb, string service)
    {
        try
        {
            // 'net' szinkron (megvárja a leállást/indulást).
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
