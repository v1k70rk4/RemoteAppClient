using System.Text;
using RemoteAgent.Admin;

namespace RemoteClient.Views;

/// <summary>
/// Renders the server health snapshot as aligned key/value text: what the Diagnostics tab shows and what an
/// admin pastes into a bug report. The labels are deliberately neutral technical identifiers rather than
/// localized copy, so a snapshot reads the same whoever took it.
/// </summary>
internal static class DiagText
{
    public static string Render(ServerDiag d)
    {
        // Invariant numbers ("34.1", not "34,1"): a snapshot is pasted into bug reports and diffed against
        // others, so it must read the same regardless of the operator's Windows locale.
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try { return RenderCore(d); }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    private static string RenderCore(ServerDiag d)
    {
        var sb = new StringBuilder();
        void Line(string key, string value) => sb.Append(key.PadRight(14)).Append(value).Append('\n');

        Line("server", $"{d.Version} on {d.Hostname} · {d.Os} · {d.Runtime}");
        Line("time", $"{Ts(d.UtcNow)} · up {Uptime(d.UptimeSeconds)} (since {Ts(d.StartedAt)})");
        Line("process", $"{d.ProcessMemoryMb} MB working set · {d.GcHeapMb} MB GC heap · {d.Threads} threads");
        if (d.LoadAverage is not null || d.MemoryTotalMb is not null)
            Line("system", $"load {d.LoadAverage ?? "-"} · memory {Num(d.MemoryAvailableMb)}/{Num(d.MemoryTotalMb)} MB available");
        foreach (var disk in d.Disks)
            Line("disk", $"{disk.Path}: {disk.FreeGb} of {disk.TotalGb} GB free");

        var db = d.Database;
        Line("database", db.Ok ? $"ok · {db.LatencyMs} ms · {db.SizeMb} MB" : $"FAILED · {db.Error}");
        foreach (var t in db.Tables.Take(8))
            Line("", $"{t.Name,-24} {t.Rows,10} rows {t.SizeMb,9} MB");

        var f = d.Fleet;
        Line("fleet", $"{f.Devices} devices · {f.Connected} connected · {f.Reporting} reporting · {f.Flaky} flaky · "
                    + $"{f.EventsLastHour} events/h · {f.PendingCommands} pending commands · last telemetry {(f.LastTelemetryAt is { } lt ? Ts(lt) : "-")}");

        var l = d.Log;
        Line("log", l.Directory is null
            ? $"memory only ({l.Error}) · {l.MemoryRecords} records"
            : $"{l.Directory} · {l.FileBytes / 1024} KB on disk · {l.MemoryRecords} in memory");
        Line("", $"last hour: {l.WarningsLastHour} warnings, {l.ErrorsLastHour} errors");

        if (d.Packages is { } pk)
        {
            Line("packages", pk.Missing.Count == 0
                ? $"{pk.Current} current · all files present"
                : $"{pk.Current} current · {pk.Missing.Count} FILES MISSING (upload them again under Release channels)");
            foreach (var m in pk.Missing) Line("", m);
        }

        Line("public url", d.PublicUrl ?? "-");
        if (d.Dns is { } dns)
            Line("dns", dns.Error is not null
                ? $"{dns.Host}: FAILED · {dns.Error}"
                : $"{dns.Host} -> {string.Join(", ", dns.Resolved)} · local {string.Join(", ", dns.Local)} · matches local: {(dns.MatchesLocal is true ? "yes" : "no")}");
        if (d.Tls is { } tls)
            Line("tls", tls.Ok
                ? $"ok · {tls.Subject} · issuer {tls.Issuer} · expires {tls.NotAfter:yyyy-MM-dd} ({tls.DaysLeft} days) · serial {tls.Serial}"
                : $"FAILED · {tls.Error}");
        if (d.Update is { } u)
            Line("update", $"helper {(u.HelperReady ? "ready" : "MISSING")} · staged tar {(u.StagedTar ? $"yes ({u.StagedTarSize / 1024 / 1024} MB)" : "no")}"
                         + $" · sql {(u.StagedSql ? "yes" : "no")} · backup {(u.BackupAvailable ? "yes" : "no")}"
                         + $" · last {(u.LastResult is { } r ? (r.Ok ? "ok " : "FAILED ") + r.At : "-")}");
        Line("requested", d.RequestVia);
        return sb.ToString();
    }

    private static string Ts(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    private static string Num(long? n) => n?.ToString() ?? "-";

    private static string Uptime(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours:00}:{t.Minutes:00}" : $"{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }
}
