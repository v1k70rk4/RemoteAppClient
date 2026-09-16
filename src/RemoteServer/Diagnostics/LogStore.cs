using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace RemoteServer.Diagnostics;

/// <summary>One log entry as the store keeps it. <see cref="Text"/> is the message plus, when present, the
/// exception, exactly as written to the file (continuation lines indented by four spaces).</summary>
public sealed record LogRecord(DateTimeOffset At, LogLevel Level, string Category, string Text);

/// <summary>
/// The server's own log, kept where the server itself can read it back.
///
/// Why this exists: on the box the process writes to stdout, journald swallows it, and the unprivileged
/// service user cannot read the journal - so until now the only way to see the server's log was root on the
/// box. Production has no root for the developers on purpose. This provider writes the same records to a
/// daily-rolling file under a directory the service user owns (no sudo involved) and keeps the newest few
/// thousand in memory as a fallback, and <c>/admin/server/logs</c> serves them over the admin session.
/// Records are written by a single background writer, so logging never blocks a request thread.
/// </summary>
public sealed partial class LogStore : IDisposable
{
    public const int RingSize = 4096;
    private const long MaxReadBytes = 32L * 1024 * 1024;   // read at most the newest 32 MB of a day file

    private readonly LogRecord[] _ring = new LogRecord[RingSize];
    private int _ringNext, _ringCount;
    private readonly object _ringLock = new();

    private readonly Channel<(LogRecord? Record, TaskCompletionSource? Flush)> _queue =
        Channel.CreateBounded<(LogRecord?, TaskCompletionSource?)>(new BoundedChannelOptions(50_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _writer;
    private StreamWriter? _file;
    private DateOnly _fileDay;

    /// <summary>Directory of the rolling files, or null when it could not be created or written.</summary>
    public string? Directory { get; }
    /// <summary>Why file logging is off, for the diagnostics snapshot.</summary>
    public string? DirectoryError { get; }
    public int RetentionDays { get; }

    public LogStore(string? directory, int retentionDays)
    {
        RetentionDays = Math.Clamp(retentionDays, 1, 365);
        if (string.IsNullOrWhiteSpace(directory)) { DirectoryError = "no log directory configured"; return; }
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            // Prove we can append before claiming the directory works; the error message goes to the snapshot.
            using (new FileStream(FilePath(directory, DateOnly.FromDateTime(DateTime.UtcNow)), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
            Directory = directory;
            Prune();
            _writer = Task.Run(WriteLoopAsync);
        }
        catch (Exception ex)
        {
            DirectoryError = ex.Message;
        }
    }

    public static string FilePath(string dir, DateOnly day) => Path.Combine(dir, $"server-{day:yyyy-MM-dd}.log");

    /// <summary>Called by the logger for every record: ring first (never fails), then the file queue.</summary>
    public void Append(LogRecord r)
    {
        lock (_ringLock)
        {
            _ring[_ringNext] = r;
            _ringNext = (_ringNext + 1) % RingSize;
            if (_ringCount < RingSize) _ringCount++;
        }
        if (Directory is not null) _queue.Writer.TryWrite((r, null));
    }

    /// <summary>Waits until everything queued so far is on disk, so a read right after a write sees it.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (Directory is null) return;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite((null, tcs))) return;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        // Best effort: under a write storm the marker may be dropped or delayed; a slightly stale read beats a 500.
        try { await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct); }
        catch (TimeoutException) { }
    }

    private async Task WriteLoopAsync()
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token))
            {
                while (reader.TryRead(out var item))
                {
                    if (item.Record is { } r)
                    {
                        try { WriteToFile(r); } catch { /* disk trouble must never take the server down */ }
                    }
                    if (item.Flush is { } f)
                    {
                        try { _file?.Flush(); } catch { }
                        f.TrySetResult();
                    }
                }
                try { _file?.Flush(); } catch { }
            }
        }
        catch (OperationCanceledException) { }
        try { _file?.Flush(); _file?.Dispose(); } catch { }
    }

    private void WriteToFile(LogRecord r)
    {
        var day = DateOnly.FromDateTime(r.At.UtcDateTime);
        if (_file is null || day != _fileDay)
        {
            _file?.Dispose();
            var fs = new FileStream(FilePath(Directory!, day), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _file = new StreamWriter(fs, new UTF8Encoding(false)) { NewLine = "\n" };
            _fileDay = day;
            Prune();
        }
        _file.Write(Format(r));
    }

    /// <summary>Deletes day files older than the retention window.</summary>
    private void Prune()
    {
        try
        {
            var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-RetentionDays);
            foreach (var f in System.IO.Directory.EnumerateFiles(Directory!, "server-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (name.Length == "server-yyyy-MM-dd".Length
                    && DateOnly.TryParseExact(name["server-".Length..], "yyyy-MM-dd", out var d) && d < cutoff)
                    File.Delete(f);
            }
        }
        catch { /* best effort */ }
    }

    // ---- format / parse ------------------------------------------------------------------------------

    public static string Short(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
        LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "???",
    };

    public static LogLevel? ParseLevel(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" => null,
        "trc" or "trace" => LogLevel.Trace,
        "dbg" or "debug" => LogLevel.Debug,
        "inf" or "info" or "information" => LogLevel.Information,
        "wrn" or "warn" or "warning" => LogLevel.Warning,
        "err" or "error" => LogLevel.Error,
        "crt" or "crit" or "critical" => LogLevel.Critical,
        _ => null,
    };

    /// <summary>Builds the record text: the message, then the exception, continuation lines indented by four
    /// spaces so a reader can tell where one record ends and the next begins.</summary>
    public static string Compose(string message, Exception? ex)
    {
        var text = ex is null ? message : (message.Length == 0 ? ex.ToString() : message + "\n" + ex);
        return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\n    ");
    }

    /// <summary>One record as written to the file: <c>2026-09-16T18:03:17.017Z WRN Category text</c>.</summary>
    public static string Format(LogRecord r) =>
        $"{r.At.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} {Short(r.Level)} {r.Category} {r.Text}\n";

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z) (TRC|DBG|INF|WRN|ERR|CRT) (\S+) ?(.*)$")]
    private static partial Regex HeaderLine();

    /// <summary>Parses file lines back into records; lines that do not start a record continue the previous one.</summary>
    public static List<LogRecord> Parse(IEnumerable<string> lines)
    {
        var list = new List<LogRecord>();
        DateTimeOffset at = default; LogLevel level = LogLevel.None; string cat = ""; StringBuilder? text = null;
        void Close() { if (text is not null) list.Add(new LogRecord(at, level, cat, text.ToString())); text = null; }
        foreach (var line in lines)
        {
            // Compose() indents every continuation line, so a truly empty line is never part of a record:
            // it is the artifact of splitting text that ends with a newline, or a torn write.
            if (line.Length == 0) continue;
            var m = HeaderLine().Match(line);
            if (m.Success && DateTimeOffset.TryParse(m.Groups[1].Value, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var ts))
            {
                Close();
                at = ts; level = ParseLevel(m.Groups[2].Value) ?? LogLevel.None; cat = m.Groups[3].Value;
                text = new StringBuilder(m.Groups[4].Value);
            }
            else if (text is not null)
            {
                text.Append('\n').Append(line);
            }
            // a continuation line before any header (a truncated read) is dropped
        }
        Close();
        return list;
    }

    // ---- query ----------------------------------------------------------------------------------------

    /// <summary>Newest <paramref name="tail"/> records matching the filters, oldest first, plus where they came from.</summary>
    public (List<LogRecord> Records, string Source) Query(DateOnly? day, DateTimeOffset? since, LogLevel? minLevel, string? contains, int tail)
    {
        bool Keep(LogRecord r) =>
            (minLevel is null || r.Level >= minLevel)
            && (since is null || r.At >= since)
            && (string.IsNullOrEmpty(contains)
                || r.Text.Contains(contains, StringComparison.OrdinalIgnoreCase)
                || r.Category.Contains(contains, StringComparison.OrdinalIgnoreCase));

        if (Directory is null)
        {
            LogRecord[] snapshot;
            lock (_ringLock)
            {
                snapshot = new LogRecord[_ringCount];
                for (int i = 0; i < _ringCount; i++) snapshot[i] = _ring[(_ringNext - _ringCount + i + RingSize) % RingSize];
            }
            var fromRing = snapshot.Where(Keep).ToList();
            if (fromRing.Count > tail) fromRing.RemoveRange(0, fromRing.Count - tail);
            return (fromRing, $"memory (file logging unavailable: {DirectoryError})");
        }

        // Newest day first, stop once the tail is full; a day filter reads that one file only.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var days = day is { } d ? [d] : new List<DateOnly> { today, today.AddDays(-1), today.AddDays(-2) };
        var result = new List<LogRecord>();
        foreach (var dd in days)
        {
            var path = FilePath(Directory, dd);
            if (!File.Exists(path)) continue;
            var records = Parse(ReadTail(path)).Where(Keep).ToList();
            result.InsertRange(0, records);
            if (result.Count >= tail) break;
            if (since is { } s && dd.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) < s.UtcDateTime.Date) break;
        }
        if (result.Count > tail) result.RemoveRange(0, result.Count - tail);
        return (result, "file:" + Directory);
    }

    /// <summary>Reads the newest part of a day file that is possibly still being appended to.</summary>
    private static IEnumerable<string> ReadTail(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        bool skipFirst = false;
        if (fs.Length > MaxReadBytes) { fs.Seek(fs.Length - MaxReadBytes, SeekOrigin.Begin); skipFirst = true; }
        using var sr = new StreamReader(fs, Encoding.UTF8);
        string? line;
        if (skipFirst) sr.ReadLine();   // partial line
        while ((line = sr.ReadLine()) is not null) yield return line;
    }

    /// <summary>Warnings and errors since a point in time, from the in-memory ring (cheap, no disk).</summary>
    public (int Warnings, int Errors, int Records) CountSince(DateTimeOffset since)
    {
        int w = 0, e = 0, n;
        lock (_ringLock)
        {
            n = _ringCount;
            for (int i = 0; i < _ringCount; i++)
            {
                var r = _ring[(_ringNext - _ringCount + i + RingSize) % RingSize];
                if (r.At < since) continue;
                if (r.Level == LogLevel.Warning) w++;
                else if (r.Level >= LogLevel.Error) e++;
            }
        }
        return (w, e, n);
    }

    public long FileBytes()
    {
        if (Directory is null) return 0;
        try { return System.IO.Directory.EnumerateFiles(Directory, "server-*.log").Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { _writer?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}

/// <summary>Feeds every logger category into the <see cref="LogStore"/>; level filtering is the host's
/// (appsettings Logging:LogLevel applies to this provider like any other).</summary>
public sealed class LogStoreProvider(LogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new StoreLogger(store, categoryName);
    public void Dispose() { }

    private sealed class StoreLogger(LogStore store, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.None) return;
            store.Append(new LogRecord(DateTimeOffset.UtcNow, logLevel, category, LogStore.Compose(formatter(state, exception), exception)));
        }
    }
}
