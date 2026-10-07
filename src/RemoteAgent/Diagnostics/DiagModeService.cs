using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Diagnostics;

/// <summary>
/// Says at startup where the file log is and whether verbose logging is on, and switches verbose logging
/// off when its time is up - the one thing <see cref="DiagMode"/> cannot do from a file alone. The TightVNC
/// side follows by itself: its log level is part of the VNC hardening, so the VNC watchdog re-applies it
/// (restarting tvnserver) within half a minute of either change.
/// </summary>
public sealed class DiagModeService(FileLogProvider fileLog, ILogger<DiagModeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (fileLog.Directory is { } dir) logger.LogInformation(L.FileLog_Started, dir, fileLog.RetentionDays);
        else logger.LogWarning(L.FileLog_Unavailable, fileLog.DirectoryError);

        if (DiagMode.Until is { } until)
        {
            if (until > DateTimeOffset.UtcNow) logger.LogInformation(L.DiagMode_ActiveAtStart, until);
            else Expire();
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
            if (DiagMode.Until is { } u && u <= DateTimeOffset.UtcNow) Expire();
        }
    }

    private void Expire()
    {
        try { DiagMode.Disable(); logger.LogInformation(L.DiagMode_Expired); }
        catch (Exception ex) { logger.LogWarning(ex, L.DiagMode_Failed); }
    }
}
