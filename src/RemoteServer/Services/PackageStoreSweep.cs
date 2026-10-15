using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RemoteServer.Data;
using RemoteServer.Data.Entities;

namespace RemoteServer.Services;

/// <summary>
/// Keeps the package store small. Every upload adds a record and a file and nothing ever removed them, so the
/// store held every version ever uploaded (tens of 70 MB exes). The sweep keeps, per channel and component,
/// the current package and the one before it (a rollout in flight may still be downloading the previous one),
/// drops the older records, and deletes the package files no remaining record points at. A file shared by
/// several channels (promote copies the record, not the file) stays as long as any of them keeps it. Only files
/// named like packages are touched: generated MSIs, the msi-build folder and anything else in the directory are
/// left alone. Runs at start-up and after each upload.
/// </summary>
public static partial class PackageStoreSweep
{
    public const int KeepPerLane = 2;

    /// <summary>Held by the sweep for its whole run and by an upload from the moment its file lands until its
    /// record is saved: a file without a record yet would otherwise look like an orphan to a sweep running at
    /// the same time (another upload's, or start-up's) and be deleted under the upload.</summary>
    public static readonly SemaphoreSlim Gate = new(1, 1);

    [GeneratedRegex(@"^(agent|updater|client|vnc)-[a-z0-9_]+-", RegexOptions.IgnoreCase)]
    private static partial Regex PackageName();

    /// <summary>Which records and which files the sweep would remove. Pure, so it is testable.</summary>
    public static (List<ReleasePackage> StaleRecords, List<string> StaleFiles) Plan(
        IEnumerable<ReleasePackage> records, IEnumerable<string> fileNames, int keep = KeepPerLane)
    {
        var stale = new List<ReleasePackage>();
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lane in records.GroupBy(p => (Channel: p.Channel.ToLowerInvariant(), Component: p.Component.ToLowerInvariant())))
        {
            var ordered = lane.OrderByDescending(p => p.UploadedAt).ThenBy(p => p.Id).ToList();
            foreach (var p in ordered.Take(keep)) kept.Add(p.FileName);
            stale.AddRange(ordered.Skip(keep));
        }
        var files = fileNames.Where(f => PackageName().IsMatch(f) && !kept.Contains(f)).ToList();
        return (stale, files);
    }

    /// <summary>Applies the plan to the database and the directory. Returns what it removed.</summary>
    public static async Task<(int Records, int Files, long Bytes)> RunAsync(AppDbContext db, string dir, ILogger logger, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return (0, 0, 0);
        await Gate.WaitAsync(ct);
        try
        {
            var records = await db.ReleasePackages.ToListAsync(ct);
            var names = Directory.EnumerateFiles(dir).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!);
            var (staleRecords, staleFiles) = Plan(records, names);

            if (staleRecords.Count > 0)
            {
                db.ReleasePackages.RemoveRange(staleRecords);
                await db.SaveChangesAsync(ct);
            }
            long bytes = 0; int deleted = 0;
            foreach (var f in staleFiles)
            {
                var path = Path.Combine(dir, f);
                try
                {
                    bytes += new FileInfo(path).Length;
                    File.Delete(path);
                    deleted++;
                }
                catch (Exception ex) { logger.LogWarning(ex, "Package sweep could not delete {File}", f); }
            }
            if (staleRecords.Count > 0 || deleted > 0)
                logger.LogInformation("Package sweep: {Records} old records and {Files} files ({MB} MB) removed; the current and previous package of every channel are kept",
                    staleRecords.Count, deleted, bytes / 1_048_576);
            return (staleRecords.Count, deleted, bytes);
        }
        finally { Gate.Release(); }
    }
}
