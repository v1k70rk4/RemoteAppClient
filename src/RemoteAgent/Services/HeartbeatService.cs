using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Configuration;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Services;

/// <summary>
/// Periodikus életjel-fájl (&lt;EnrollmentDir&gt;\agent.heartbeat) frissítése. A Helper
/// (RemoteAgent.Updater) ezt figyeli: ha az életjel elöregszik, miközben a service
/// "fut" állapotban van, akkor az agent BERAGADT — az SCM ezt nem látja, csak a
/// processz-kilépést. Ilyenkor a Helper stop→(kell esetén)kill→restart ciklussal
/// helyreállít. Szándékosan a legolcsóbb jelzés: egy fájl időbélyege, semmi IPC.
/// </summary>
public sealed class HeartbeatService(IOptions<AgentOptions> options, ILogger<HeartbeatService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private readonly string _file = Path.Combine(options.Value.EnrollmentDir, "agent.heartbeat");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(_file)!); } catch { /* best effort */ }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await File.WriteAllTextAsync(_file, DateTimeOffset.UtcNow.ToString("O"), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogDebug(ex, L.HeartbeatService_001); }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
