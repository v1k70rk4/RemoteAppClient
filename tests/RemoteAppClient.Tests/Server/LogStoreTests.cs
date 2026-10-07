using Microsoft.Extensions.Logging;
using RemoteServer.Diagnostics;

namespace RemoteAppClient.Tests.Server;

/// <summary>The server's own log file: one format, parsed back exactly, pruned by age, memory when the disk is not there.</summary>
public class LogStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rac-tests-" + Guid.NewGuid().ToString("N"));
    public LogStoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, 123, TimeSpan.Zero);

    [Fact]
    public void Compose_indents_every_continuation_line_of_the_exception()
    {
        var text = LogStore.Compose("boom", new InvalidOperationException("line one\r\nline two"));
        var lines = text.Split('\n');
        Assert.Equal("boom", lines[0]);
        Assert.StartsWith("    System.InvalidOperationException: line one", lines[1]);
        Assert.Equal("    line two", lines[2]);
        Assert.DoesNotContain("\r", text);
    }

    [Fact]
    public void Format_and_parse_round_trip()
    {
        var a = new LogRecord(T0, LogLevel.Warning, "RemoteServer.X", LogStore.Compose("first", new Exception("why\nnot")));
        var b = new LogRecord(T0.AddSeconds(1), LogLevel.Information, "Y", "second");
        var text = LogStore.Format(a) + LogStore.Format(b);
        Assert.StartsWith("2026-10-01T12:00:00.123Z WRN RemoteServer.X first\n    System.Exception: why\n    not\n", text);

        var back = LogStore.Parse(text.Split('\n'));
        Assert.Equal(2, back.Count);
        Assert.Equal(a, back[0]);
        Assert.Equal(b, back[1]);
    }

    [Fact]
    public void A_torn_read_drops_the_leading_continuation_lines()
    {
        var back = LogStore.Parse(new[] { "    stray tail of an earlier record", "", LogStore.Format(new LogRecord(T0, LogLevel.Error, "C", "ok")).TrimEnd('\n') });
        Assert.Single(back);
        Assert.Equal("ok", back[0].Text);
    }

    [Theory]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("WRN", LogLevel.Warning)]
    [InlineData("dbg", LogLevel.Debug)]
    [InlineData("information", LogLevel.Information)]
    [InlineData("crit", LogLevel.Critical)]
    public void Level_names_are_lenient(string s, LogLevel expected) => Assert.Equal(expected, LogStore.ParseLevel(s));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("all")]
    [InlineData("loud")]
    public void Unknown_or_empty_level_means_no_filter(string? s) => Assert.Null(LogStore.ParseLevel(s));

    [Fact]
    public async Task Records_land_in_todays_file_and_queries_filter_them()
    {
        var dir = Path.Combine(_root, "logs");
        using var store = new LogStore(dir, 14);
        Assert.Equal(dir, store.Directory);
        Assert.Null(store.DirectoryError);

        var now = DateTimeOffset.UtcNow;
        store.Append(new LogRecord(now.AddSeconds(-3), LogLevel.Information, "A", "hello"));
        store.Append(new LogRecord(now.AddSeconds(-2), LogLevel.Warning, "B", "careful"));
        store.Append(new LogRecord(now.AddSeconds(-1), LogLevel.Error, "C", "Broken thing"));
        await store.FlushAsync();

        Assert.True(File.Exists(LogStore.FilePath(dir, DateOnly.FromDateTime(now.UtcDateTime))));

        var (all, source) = store.Query(null, null, null, null, 100);
        Assert.Equal(3, all.Count);
        Assert.StartsWith("file:", source);

        var (warnings, _) = store.Query(null, null, LogLevel.Warning, null, 100);
        Assert.Equal(new[] { "careful", "Broken thing" }, warnings.Select(r => r.Text));

        var (found, _) = store.Query(null, null, null, "broken", 100);
        Assert.Single(found);

        var (tail, _) = store.Query(null, null, null, null, 1);
        Assert.Equal("Broken thing", Assert.Single(tail).Text);

        var (w, e, n) = store.CountSince(now.AddMinutes(-1));
        Assert.Equal((1, 1, 3), (w, e, n));
    }

    [Fact]
    public void Old_day_files_are_pruned_on_start_and_recent_ones_kept()
    {
        var dir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(dir);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var old = LogStore.FilePath(dir, today.AddDays(-20));
        var recent = LogStore.FilePath(dir, today.AddDays(-3));
        File.WriteAllText(old, "old\n");
        File.WriteAllText(recent, "recent\n");

        using (new LogStore(dir, 14)) { }

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public async Task Without_a_usable_directory_the_log_lives_in_memory_and_says_so()
    {
        var file = Path.Combine(_root, "a-file");
        File.WriteAllText(file, "x");
        using var store = new LogStore(Path.Combine(file, "logs"), 14);   // a path under a file cannot be created
        Assert.Null(store.Directory);
        Assert.NotNull(store.DirectoryError);

        store.Append(new LogRecord(DateTimeOffset.UtcNow, LogLevel.Warning, "A", "kept in memory"));
        await store.FlushAsync();   // a no-op without a directory, and must not hang
        var (records, source) = store.Query(null, null, null, null, 10);
        Assert.Single(records);
        Assert.Contains("memory", source);
    }
}
