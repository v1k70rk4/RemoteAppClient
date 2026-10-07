using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace RemoteAgent.Diagnostics;

/// <summary>
/// The agent's own log file: one file per UTC day under <c>C:\ProgramData\RemoteAgent\logs</c>, pruned after
/// a couple of weeks, in the server's log format (<c>2026-10-01T12:36:06.049Z WRN Category text</c>, the
/// exception's lines indented by four spaces). The Windows event log stays the visible log for the SYSTEM
/// service; this is the one an operator can download with the file transfer and read as a whole, and the one
/// that receives Debug records while <see cref="DiagMode"/> is on. A single background writer does the disk
/// work, so logging never blocks a service thread, and disk trouble never takes the agent down.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLogProvider : ILoggerProvider
{
    private readonly Channel<string> _queue =
        Channel.CreateBounded<string>(new BoundedChannelOptions(20_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _writer;
    private StreamWriter? _file;
    private DateOnly _fileDay;

    /// <summary>Directory of the day files, or null when it could not be created or written.</summary>
    public string? Directory { get; }
    /// <summary>Why file logging is off, for the startup log line.</summary>
    public string? DirectoryError { get; }
    public int RetentionDays { get; }

    public FileLogProvider(string directory, int retentionDays)
    {
        RetentionDays = Math.Clamp(retentionDays, 1, 365);
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            // Prove we can append before claiming the directory works.
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

    public static string FilePath(string dir, DateOnly day) => Path.Combine(dir, $"agent-{day:yyyy-MM-dd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Append(LogLevel level, string category, string message, Exception? exception)
    {
        if (Directory is null) return;
        _queue.Writer.TryWrite(Format(DateTimeOffset.UtcNow, level, category, Compose(message, exception)));
    }

    private async Task WriteLoopAsync()
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token))
            {
                while (reader.TryRead(out var line))
                {
                    try { WriteToFile(line); } catch { /* disk trouble must never take the agent down */ }
                }
                try { _file?.Flush(); } catch { }
            }
        }
        catch (OperationCanceledException) { }
        try { _file?.Flush(); _file?.Dispose(); } catch { }
    }

    private void WriteToFile(string line)
    {
        // The day is in the line's first ten characters; rolling on it keeps the writer free of clocks.
        var day = DateOnly.ParseExact(line.AsSpan(0, 10), "yyyy-MM-dd");
        if (_file is null || day != _fileDay)
        {
            _file?.Dispose();
            var fs = new FileStream(FilePath(Directory!, day), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _file = new StreamWriter(fs, new UTF8Encoding(false)) { NewLine = "\n" };
            _fileDay = day;
            Prune();
        }
        _file.Write(line);
    }

    /// <summary>Deletes day files older than the retention window.</summary>
    private void Prune()
    {
        try
        {
            var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-RetentionDays);
            foreach (var f in System.IO.Directory.EnumerateFiles(Directory!, "agent-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (name.Length == "agent-yyyy-MM-dd".Length
                    && DateOnly.TryParseExact(name["agent-".Length..], "yyyy-MM-dd", out var d) && d < cutoff)
                    File.Delete(f);
            }
        }
        catch { /* best effort */ }
    }

    public static string Short(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
        LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "???",
    };

    /// <summary>The message, then the exception, continuation lines indented so a reader can tell records apart.</summary>
    public static string Compose(string message, Exception? ex)
    {
        var text = ex is null ? message : (message.Length == 0 ? ex.ToString() : message + "\n" + ex);
        return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\n    ");
    }

    public static string Format(DateTimeOffset at, LogLevel level, string category, string text) =>
        $"{at.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} {Short(level)} {category} {text}\n";

    public void Dispose()
    {
        // Completing the channel lets the writer drain what is queued and stop by itself; cancelling right
        // away could drop the last lines, which are the ones that explain a stop. Cancel only if it hangs.
        _queue.Writer.TryComplete();
        try { if (_writer is not null && !_writer.Wait(TimeSpan.FromSeconds(2))) _cts.Cancel(); } catch { }
        _cts.Dispose();
    }

    /// <summary>Level filtering is the host's (Logging:LogLevel applies to this provider under the alias "File").</summary>
    private sealed class FileLogger(FileLogProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.None) return;
            owner.Append(logLevel, category, formatter(state, exception), exception);
        }
    }
}
