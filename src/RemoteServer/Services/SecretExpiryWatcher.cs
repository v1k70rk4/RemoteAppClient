using Microsoft.EntityFrameworkCore;
using RemoteServer.Data;

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
            catch (Exception ex) { logger.LogWarning(ex, "Secret-lejárat ellenőrzés hiba."); }

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
            ? $"A RemoteAppClient levélküldési (Graph) client secret-je LEJÁRT ({when}). Frissítsd a Szerver beállításokban, különben nem megy az e-mail küldés."
            : $"A RemoteAppClient levélküldési (Graph) client secret-je {(int)days} napon belül lejár ({when}). Hozz létre újat az Azure-ban, és frissítsd a Szerver beállításokban.";

        var (ok, err) = await email.SendAsync(to!, "RemoteAppClient: a levélküldési secret hamarosan lejár", body, ct);
        if (ok)
        {
            s.SecretExpiryNotifiedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Secret-lejárat figyelmeztető elküldve: {To} ({Days} nap).", to, (int)days);
        }
        else
        {
            logger.LogWarning("Secret-lejárat figyelmeztető küldése sikertelen: {Error}", err);
        }
    }
}
