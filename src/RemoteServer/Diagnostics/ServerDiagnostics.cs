using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RemoteAgent.Admin;
using RemoteServer.Configuration;
using RemoteServer.Data;
using RemoteServer.Data.Entities;
using RemoteServer.Hub;
using RemoteServer.Services;

namespace RemoteServer.Diagnostics;

/// <summary>
/// Collects the health snapshot behind <c>/admin/server/diag</c>. Every probe is independent and reports its
/// own failure inline, so one broken subsystem (DB down, DNS timing out) still yields a snapshot of the rest -
/// which is exactly when a snapshot is wanted. Nothing here needs privileges beyond the service user's own.
/// </summary>
public static class ServerDiagnostics
{
    public static async Task<ServerDiag> CollectAsync(AppDbContext db, AgentConnectionRegistry registry, LogStore logs,
        ServerOptions opt, ServerUpdateStatus? update, string requestVia, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var proc = Process.GetCurrentProcess();
        // The OS's start time, not a static initialised on first use: that one made every fresh server
        // report an uptime of zero at the first snapshot.
        DateTimeOffset startedAt;
        try { startedAt = new DateTimeOffset(proc.StartTime.ToUniversalTime()); }
        catch { startedAt = now; }
        var diag = new ServerDiag
        {
            Version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?",
            Hostname = Environment.MachineName,
            Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            UtcNow = now,
            StartedAt = startedAt,
            UptimeSeconds = Math.Max(0, (long)(now - startedAt).TotalSeconds),
            ProcessMemoryMb = proc.WorkingSet64 / 1024 / 1024,
            GcHeapMb = GC.GetTotalMemory(false) / 1024 / 1024,
            Threads = proc.Threads.Count,
            PublicUrl = string.IsNullOrWhiteSpace(opt.PublicUrl) ? null : opt.PublicUrl,
            Update = update,
            RequestVia = requestVia,
        };

        ReadProc(diag);
        diag.Disks = Disks(logs.Directory, opt.PackagesDir, opt.UpdatesDir);
        diag.Log = LogInfo(logs, now);

        // The network probes (DNS, TLS) run alongside the database ones, each with its own timeout. The two
        // database probes share one DbContext, which is not thread-safe, so they run one after the other.
        var host = HostOf(opt.PublicUrl);
        var dnsTask = host is null ? Task.FromResult<ServerDiagDns?>(null) : DnsAsync(host, ct);
        var tlsTask = host is null ? Task.FromResult<ServerDiagTls?>(null) : TlsAsync(host, ct);
        diag.Database = await DatabaseAsync(db, ct);
        diag.Fleet = await FleetAsync(db, registry, now, ct);
        try
        {
            var (current, missing) = await CurrentPackagesAsync(db, opt.PackagesDir, ct);
            diag.Packages = new ServerDiagPackages { Current = current, Missing = missing };
        }
        catch { /* the database part already reports the DB error */ }
        await Task.WhenAll(dnsTask, tlsTask);
        diag.Dns = dnsTask.Result;
        diag.Tls = tlsTask.Result;
        return diag;
    }

    /// <summary>
    /// The package each channel currently serves per component, and which of them have no file on disk. Rows
    /// survive a restore (they are in the database); the files do not (the package directory is deliberately
    /// not in the backup), so this is the check that tells a freshly moved server what still has to be uploaded.
    /// </summary>
    public static async Task<(int Current, List<string> Missing)> CurrentPackagesAsync(AppDbContext db, string packagesDir, CancellationToken ct)
    {
        var rows = await db.ReleasePackages.AsNoTracking().ToListAsync(ct);
        var current = rows
            .GroupBy(p => (p.Channel, p.Component))
            .Select(g => g.OrderByDescending(p => p.UploadedAt).First())
            .OrderBy(p => p.Channel).ThenBy(p => p.Component)
            .ToList();
        var missing = current
            .Where(p => !File.Exists(Path.Combine(packagesDir, p.FileName)))
            .Select(p => $"{p.Channel}/{p.Component} {p.Version} ({p.FileName})")
            .ToList();
        return (current.Count, missing);
    }

    private static string? HostOf(string? publicUrl)
    {
        if (string.IsNullOrWhiteSpace(publicUrl)) return null;
        return Uri.TryCreate(publicUrl, UriKind.Absolute, out var u) ? u.Host : null;
    }

    /// <summary>Linux load and memory from /proc; silently absent elsewhere.</summary>
    private static void ReadProc(ServerDiag d)
    {
        try
        {
            if (File.Exists("/proc/loadavg"))
            {
                var parts = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3) d.LoadAverage = $"{parts[0]} {parts[1]} {parts[2]}";
            }
            if (File.Exists("/proc/meminfo"))
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) d.MemoryTotalMb = KbToMb(line);
                    else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) d.MemoryAvailableMb = KbToMb(line);
                }
            }
        }
        catch { /* not fatal */ }

        static long? KbToMb(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], out var kb) ? kb / 1024 : null;
        }
    }

    private static List<ServerDiagDisk> Disks(params string?[] paths)
    {
        var list = new List<ServerDiagDisk>();
        var seen = new HashSet<string>();
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try
            {
                var probe = System.IO.Directory.Exists(p) ? p : (Path.GetDirectoryName(p) ?? p);
                if (OperatingSystem.IsWindows()) probe = Path.GetPathRoot(probe) ?? probe;
                var di = new DriveInfo(probe);
                if (!seen.Add(di.Name)) continue;
                list.Add(new ServerDiagDisk
                {
                    Path = OperatingSystem.IsWindows() ? di.Name : p,
                    TotalGb = Math.Round(di.TotalSize / 1024.0 / 1024 / 1024, 1),
                    FreeGb = Math.Round(di.AvailableFreeSpace / 1024.0 / 1024 / 1024, 1),
                });
            }
            catch { /* path not mounted here */ }
        }
        return list;
    }

    private static ServerDiagLog LogInfo(LogStore logs, DateTimeOffset now)
    {
        var (w, e, n) = logs.CountSince(now.AddHours(-1));
        return new ServerDiagLog
        {
            Directory = logs.Directory,
            Error = logs.DirectoryError,
            FileBytes = logs.FileBytes(),
            MemoryRecords = n,
            WarningsLastHour = w,
            ErrorsLastHour = e,
        };
    }

    /// <summary>Round-trip latency plus per-table size from information_schema (the app's own DB user can read it).</summary>
    private static async Task<ServerDiagDb> DatabaseAsync(AppDbContext db, CancellationToken ct)
    {
        var r = new ServerDiagDb();
        try
        {
            var sw = Stopwatch.StartNew();
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
            await using (var ping = conn.CreateCommand())
            {
                ping.CommandText = "SELECT 1";
                await ping.ExecuteScalarAsync(ct);
            }
            r.LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT table_name, IFNULL(table_rows, 0), IFNULL(data_length, 0) + IFNULL(index_length, 0) "
                            + "FROM information_schema.tables WHERE table_schema = DATABASE() ORDER BY 3 DESC";
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            double total = 0;
            while (await rd.ReadAsync(ct))
            {
                var bytes = Convert.ToDouble(rd.GetValue(2));
                total += bytes;
                r.Tables.Add(new ServerDiagTable
                {
                    Name = rd.GetString(0),
                    Rows = Convert.ToInt64(rd.GetValue(1)),
                    SizeMb = Math.Round(bytes / 1024 / 1024, 2),
                });
            }
            r.SizeMb = Math.Round(total / 1024 / 1024, 1);
            r.Ok = true;
        }
        catch (Exception ex) { r.Ok = false; r.Error = ex.Message; }
        return r;
    }

    private static async Task<ServerDiagFleet> FleetAsync(AppDbContext db, AgentConnectionRegistry registry, DateTimeOffset now, CancellationToken ct)
    {
        var f = new ServerDiagFleet();
        try
        {
            var fresh = now - DeviceLiveness.FreshWindow;
            f.Devices = await db.Devices.CountAsync(d => !d.DeviceId.StartsWith("opsrc:"), ct);
            f.Reporting = await db.Devices.CountAsync(d => d.LastSeenAt > fresh, ct);
            f.LastTelemetryAt = await db.Devices.MaxAsync(d => d.LastSeenAt, ct);
            f.EventsLastHour = await db.DeviceEvents.CountAsync(e => e.At > now.AddHours(-1), ct);
            f.PendingCommands = await db.Commands.CountAsync(c => c.Status == CommandStatus.Queued || c.Status == CommandStatus.Sent, ct);
            var connected = registry.ConnectedDevices;
            f.Connected = connected.Count;
            f.Flaky = connected.Count(id => registry.RecentReconnects(id) >= DeviceInfo.FlakyReconnectThreshold);
        }
        catch { /* the database part already reports the DB error */ }
        return f;
    }

    /// <summary>What the public name resolves to versus the addresses on this box. A mismatch is normal behind
    /// NAT, but a name still pointing at the previous server is the first thing to rule out after a move.</summary>
    private static async Task<ServerDiagDns?> DnsAsync(string host, CancellationToken ct)
    {
        var r = new ServerDiagDns { Host = host };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var a = ua.Address;
                    if (IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal) continue;
                    r.Local.Add(a.ToString());
                }
            }
        }
        catch { /* interfaces unreadable: still try DNS */ }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            var addrs = await Dns.GetHostAddressesAsync(host, cts.Token);
            r.Resolved = addrs.Select(a => a.ToString()).OrderBy(s => s, StringComparer.Ordinal).ToList();
            r.MatchesLocal = r.Resolved.Intersect(r.Local).Any();
        }
        catch (Exception ex) { r.Error = ex.Message; }
        return r;
    }

    /// <summary>Handshakes with the public 443 the way an agent would and reports the served certificate.</summary>
    private static async Task<ServerDiagTls?> TlsAsync(string host, CancellationToken ct)
    {
        var r = new ServerDiagTls();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, 443, cts.Token);
            // We only want to LOOK at the certificate, whatever chain state it has.
            await using var ssl = new SslStream(tcp.GetStream(), false, static (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token);
            if (ssl.RemoteCertificate is { } cert)
            {
                using var x = new X509Certificate2(cert);
                r.Subject = x.Subject;
                r.Issuer = x.Issuer;
                r.NotAfter = new DateTimeOffset(x.NotAfter.ToUniversalTime());
                r.DaysLeft = (int)Math.Floor((x.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays);
                r.Serial = x.SerialNumber;
            }
            r.Ok = true;
        }
        catch (Exception ex) { r.Ok = false; r.Error = ex.Message; }
        return r;
    }
}
