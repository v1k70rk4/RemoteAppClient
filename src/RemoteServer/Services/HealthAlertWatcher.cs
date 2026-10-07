using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RemoteServer.Configuration;
using RemoteServer.Data;
using RemoteServer.Diagnostics;
using RemoteServer.Hub;
using L = RemoteServer.Localization.Strings;

namespace RemoteServer.Services;

/// <summary>
/// Watches the server's own health and says so by e-mail: disks, the database, the public TLS certificate,
/// package files, the log directory, the last self-update, device certificates nearing their end, and a fleet
/// that reports telemetry while nothing is on the command channel. The snapshot is the one the console's
/// Diagnostics tab shows; this is the part that does not wait for somebody to open that tab.
///
/// Mail goes out on change: when a check starts failing, again every <see cref="AlertOptions.RepeatHours"/>
/// while it keeps failing, and once when it clears. Every transition is also a line in the server log, so the
/// console sees it without e-mail. What the server cannot report is its own absence: an outside probe of
/// /health is still needed for that.
/// </summary>
public sealed class HealthAlertWatcher(
    IServiceScopeFactory scopeFactory,
    AgentConnectionRegistry registry,
    LogStore logs,
    IOptions<ServerOptions> options,
    ILogger<HealthAlertWatcher> logger) : BackgroundService
{
    private sealed record Finding(string Key, string Text);

    /// <summary>Checks failing right now, with the time their last e-mail went out (MinValue: not yet, retry).</summary>
    private readonly Dictionary<string, DateTimeOffset> _failing = [];
    private bool _noRecipientLogged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var a = options.Value.Alerts;
        if (!a.Enabled) return;

        // Let the server settle (agents reconnecting, first telemetry) before judging it.
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, L.HealthAlert_CheckError); }

            try { await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(a.IntervalMinutes, 5, 1440)), stoppingToken); }
            catch { return; }
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        var o = options.Value;
        var a = o.Alerts;
        var now = DateTimeOffset.UtcNow;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();

        var settings = await db.ServerSettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        // Server-generated, user-independent mail → server language (ServerSettings.Language, or OS when "auto").
        var lang = settings is null || string.IsNullOrWhiteSpace(settings.Language) || settings.Language == "auto"
            ? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : settings.Language;
        string T(string key, params object?[] args) => L.Format(L.Get(key, lang), args);

        var diag = await ServerDiagnostics.CollectAsync(db, registry, logs, o, null, "alerts", ct);
        var findings = new List<Finding>();

        foreach (var d in diag.Disks)
        {
            if (d.TotalGb <= 0) continue;
            if (d.FreeGb < 1 || d.FreeGb / d.TotalGb * 100 < a.DiskFreePercent)
                findings.Add(new("disk:" + d.Path, T("HealthAlert_Disk", d.Path, d.FreeGb.ToString("F1"), d.TotalGb.ToString("F0"))));
        }

        if (!diag.Database.Ok) findings.Add(new("db", T("HealthAlert_DbDown", diag.Database.Error ?? "?")));
        else if (diag.Database.LatencyMs > a.DbLatencyWarnMs) findings.Add(new("db-slow", T("HealthAlert_DbSlow", diag.Database.LatencyMs.ToString("F0"))));

        if (diag.Tls is { } tls)
        {
            if (!tls.Ok) findings.Add(new("tls", T("HealthAlert_TlsFailed", tls.Error ?? "?")));
            else if (tls.DaysLeft is { } days && days <= a.TlsDaysWarn)
                findings.Add(new("tls-expiry", T("HealthAlert_TlsExpiring", days, tls.NotAfter?.ToString("yyyy-MM-dd") ?? "?")));
        }

        if (diag.Packages is { Missing.Count: > 0 } pk)
            findings.Add(new("packages", T("HealthAlert_PackagesMissing", string.Join(", ", pk.Missing))));

        if (diag.Log.Error is { } logErr)
            findings.Add(new("log", T("HealthAlert_LogDir", logErr)));

        // The helper writes result.status after every self-update; "failed" means it rolled back. The time is
        // part of the key, so a later failure alerts again and a later success clears this one.
        var statusFile = Path.Combine(o.UpdatesDir, "result.status");
        if (File.Exists(statusFile) && File.ReadAllText(statusFile).Trim() == "failed")
        {
            var atFile = Path.Combine(o.UpdatesDir, "result.at");
            var at = File.Exists(atFile) ? File.ReadAllText(atFile).Trim() : "?";
            findings.Add(new("update:" + at, T("HealthAlert_UpdateFailed", at)));
        }

        // Device certificates (and the SSH certificates issued alongside) live ClientCertValidityDays from
        // enrolment and nothing renews them yet: a device whose certificate expired cannot connect until it is
        // re-enrolled. Say so well ahead - for devices that are still around: a re-enrolment leaves the old
        // record behind with its old date, and a machine silent for a month is not waiting for this warning.
        var validity = o.ClientCertValidityDays;
        var enrolledBefore = now.AddDays(a.DeviceCertDaysWarn - validity);
        var expiredBefore = now.AddDays(-validity);
        var seenSince = now.AddDays(-30);
        var expiring = await db.Devices
            .Where(d => d.Status == DeviceStatus.Approved && !d.DeviceId.StartsWith("opsrc:")
                        && d.LastSeenAt >= seenSince && d.EnrolledAt <= enrolledBefore)
            .OrderBy(d => d.EnrolledAt)
            .Select(d => new { d.Hostname, d.EnrolledAt })
            .ToListAsync(ct);
        if (expiring.Count > 0)
        {
            var first = expiring[0];
            var expired = expiring.Count(d => d.EnrolledAt <= expiredBefore);
            findings.Add(new("device-certs", T("HealthAlert_DeviceCerts",
                expiring.Count, a.DeviceCertDaysWarn, first.Hostname, first.EnrolledAt.AddDays(validity).ToString("yyyy-MM-dd"), expired)));
        }

        // Telemetry arrives over /api while nothing holds a command channel: the 443 mux or nginx's /agent route
        // is stuck - the exact failure of the last server move, which took a day to notice.
        if (diag.Fleet.Reporting > 0 && diag.Fleet.Connected == 0)
            findings.Add(new("no-c2", T("HealthAlert_NoCommandChannel", diag.Fleet.Reporting)));

        // Transitions.
        var repeat = TimeSpan.FromHours(Math.Max(1, a.RepeatHours));
        var fresh = new List<Finding>();
        var again = new List<Finding>();
        foreach (var f in findings)
        {
            if (!_failing.TryGetValue(f.Key, out var lastSent))
            {
                fresh.Add(f);
                logger.LogWarning(L.HealthAlert_Failing, f.Key, f.Text);
            }
            else if (now - lastSent >= repeat) again.Add(f);
        }
        var cleared = _failing.Keys.Except(findings.Select(f => f.Key)).ToList();
        foreach (var key in cleared)
        {
            _failing.Remove(key);
            logger.LogInformation(L.HealthAlert_Cleared, key);
        }
        if (fresh.Count == 0 && again.Count == 0 && cleared.Count == 0) return;

        // Mail. Without a recipient the log is all there is; the state is still kept so the log does not repeat.
        var to = settings?.SupportEmail;
        if (string.IsNullOrWhiteSpace(to))
        {
            if (!_noRecipientLogged) { _noRecipientLogged = true; logger.LogInformation(L.HealthAlert_NoRecipient); }
            foreach (var f in fresh) _failing[f.Key] = now;
            return;
        }

        var lines = new List<string>
        {
            T("HealthAlert_Intro", diag.Hostname, diag.Version, now.ToString("yyyy-MM-dd HH:mm")),
            "",
        };
        void Section(string title, IEnumerable<string> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            lines.Add(title);
            foreach (var i in list) lines.Add("  - " + i);
            lines.Add("");
        }
        Section(T("HealthAlert_SectionNew"), fresh.Select(f => f.Text));
        Section(T("HealthAlert_SectionStill"), again.Select(f => f.Text));
        Section(T("HealthAlert_SectionCleared"), cleared);
        lines.Add(T("HealthAlert_Footer"));

        var subject = T("HealthAlert_Subject", diag.Hostname, findings.Count, cleared.Count);
        var (ok, err) = await email.SendAsync(to!, subject, string.Join(Environment.NewLine, lines), ct);
        if (ok)
        {
            foreach (var f in fresh.Concat(again)) _failing[f.Key] = now;
            logger.LogInformation(L.HealthAlert_Sent, to, findings.Count, cleared.Count);
        }
        else
        {
            // Remember the failure (no second "new" log line), but leave it due for mail on the next pass.
            foreach (var f in fresh) _failing[f.Key] = DateTimeOffset.MinValue;
            logger.LogWarning(L.HealthAlert_SendFailed, err);
        }
    }
}
