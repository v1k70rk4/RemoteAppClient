using Microsoft.EntityFrameworkCore;
using RemoteServer.Data;
using L = RemoteServer.Localization.Strings;

namespace RemoteServer.Services;

/// <summary>
/// Naponta ellenőrzi a Graph client secret lejáratát: ha 30 napon belül lejár (és még nem
/// figyelmeztettünk erre a lejáratra), e-mailt küld a support- (vagy feladó-) címre.
/// </summary>
public sealed class SecretExpiryWatcher(IServiceScopeFactory scopeFactory, ILogger<SecretExpiryWatcher> logger) : BackgroundService
{
    private const int WarnDays = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Indulás után kicsit várunk, majd 12 óránként ellenőrzünk.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckOnceAsync(stoppingToken); }
            catch (Exception ex) { logger.LogWarning(ex, L.SecretExpiryWatcher_001); }

            try { await Task.Delay(TimeSpan.FromHours(12), stoppingToken); } catch { return; }
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();

        var s = await db.ServerSettings.FirstOrDefaultAsync(ct);
        if (s is null || s.EmailProvider != "graph" || s.GraphSecretExpiresAt is not { } expiry) return;
        if (s.SecretExpiryNotifiedAt is not null) return; // erre a lejáratra már szóltunk

        var days = (expiry - DateTimeOffset.UtcNow).TotalDays;
        if (days > WarnDays) return; // még ráérünk

        var to = !string.IsNullOrWhiteSpace(s.SupportEmail) ? s.SupportEmail : s.GraphSender;
        if (string.IsNullOrWhiteSpace(to)) return;

        var when = expiry.LocalDateTime.ToString("yyyy.MM.dd");
        var body = days <= 0
            ? L.Format(L.SecretExpiryWatcher_002, when)
            : L.Format(L.SecretExpiryWatcher_003, (int)days, when);

        var (ok, err) = await email.SendAsync(to!, L.SecretExpiryWatcher_004, body, ct);
        if (ok)
        {
            s.SecretExpiryNotifiedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(L.SecretExpiryWatcher_005, to, (int)days);
        }
        else
        {
            logger.LogWarning(L.SecretExpiryWatcher_006, err);
        }
    }
}
