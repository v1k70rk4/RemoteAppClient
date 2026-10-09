using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteAgent.Diagnostics;

namespace RemoteAppClient.Tests.Agent;

/// <summary>
/// The agent's verbose-logging switch: one file that is both the expiry record and a reloadable logging
/// configuration, so the level changes without a restart; and the file log that receives the detail.
/// DiagMode is process-wide state, so these tests do not run alongside each other.
/// </summary>
[Collection("DiagMode")]
public class DiagModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rac-tests-" + Guid.NewGuid().ToString("N"));

    public DiagModeTests()
    {
        Directory.CreateDirectory(_root);
        DiagMode.Configure(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Off_without_a_file_and_the_normal_tightvnc_level()
    {
        Assert.Null(DiagMode.Until);
        Assert.False(DiagMode.IsActive);
        Assert.Equal(DiagMode.TightVncNormalLevel, DiagMode.TightVncLogLevel);
    }

    [Fact]
    public void Enable_writes_an_expiry_and_a_debug_override_and_disable_removes_them()
    {
        var until = DiagMode.Enable(24);
        Assert.True(File.Exists(DiagMode.FilePath));
        Assert.False(File.Exists(DiagMode.FilePath + ".tmp"));
        Assert.InRange((until - DateTimeOffset.UtcNow.AddHours(24)).TotalMinutes, -1, 1);
        Assert.NotNull(DiagMode.Until);
        Assert.InRange((DiagMode.Until!.Value - until).TotalSeconds, -1, 1);
        Assert.True(DiagMode.IsActive);
        Assert.Equal(DiagMode.TightVncVerboseLevel, DiagMode.TightVncLogLevel);

        Assert.Equal("Debug", DiagMode.LogLevelOverrides["Logging:LogLevel:Default"]);
        Assert.Equal("Information", DiagMode.LogLevelOverrides["Logging:LogLevel:Microsoft"]);

        DiagMode.Disable();
        Assert.False(File.Exists(DiagMode.FilePath));
        Assert.False(DiagMode.IsActive);
        DiagMode.Disable();   // twice is harmless
    }

    [Fact]
    public void Hours_are_clamped_to_the_maximum()
    {
        var until = DiagMode.Enable(500);
        Assert.InRange((until - DateTimeOffset.UtcNow).TotalHours, DiagMode.MaxHours - 0.1, DiagMode.MaxHours + 0.1);
    }

    [Fact]
    public void An_expired_or_corrupt_file_is_simply_off()
    {
        File.WriteAllText(DiagMode.FilePath, "{ \"Diag\": { \"Until\": \"2020-01-01T00:00:00Z\" } }");
        Assert.Equal(2020, DiagMode.Until!.Value.Year);
        Assert.False(DiagMode.IsActive);
        Assert.Equal(DiagMode.TightVncNormalLevel, DiagMode.TightVncLogLevel);

        File.WriteAllText(DiagMode.FilePath, "this is not json");
        Assert.Null(DiagMode.Until);
        Assert.False(DiagMode.IsActive);
    }

    [Fact]
    public async Task The_live_log_level_follows_the_file_without_a_restart()
    {
        var baseSettings = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(baseSettings, "{ \"Logging\": { \"LogLevel\": { \"Default\": \"Information\" }, \"EventLog\": { \"LogLevel\": { \"Default\": \"Information\" } } } }");
        var config = new ConfigurationBuilder()
            .AddJsonFile(baseSettings, optional: false, reloadOnChange: false)
            .Add(new DiagConfigurationSource())
            .Build();

        using var fileLog = new FileLogProvider(DiagMode.LogDirectory, retentionDays: 14);
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.AddConfiguration(config.GetSection("Logging"));
            b.Services.AddSingleton<ILoggerProvider>(fileLog);
            b.Services.AddSingleton<ILoggerProvider, FakeEventLogProvider>();
        });
        using var sp = services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("RemoteAgent.Test");
        var fake = (FakeEventLogProvider)sp.GetServices<ILoggerProvider>().First(p => p is FakeEventLogProvider);

        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Information));

        DiagMode.Enable(1);
        await WaitUntil(() => logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Debug), "Debug did not switch on after Enable");

        var framework = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.Hosting.Lifetime");
        Assert.False(framework.IsEnabled(LogLevel.Debug));

        logger.LogDebug("debug line");
        logger.LogInformation("info line");
        Assert.Equal((0, 1), (fake.Debugs, fake.Infos));   // the event-log provider keeps its own Information floor

        DiagMode.Disable();
        await WaitUntil(() => !logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.Debug), "Debug did not switch off after Disable");
    }

    [Fact]
    public void A_planted_file_reaches_nothing_but_the_fixed_log_levels()
    {
        // The folder is shared with the machine's users: whatever they write into diag.json besides the expiry
        // must not reach the agent's settings (an ssh path would run as SYSTEM) or the event-log provider's.
        File.WriteAllText(DiagMode.FilePath,
            "{ \"Diag\": { \"Until\": \"" + DateTimeOffset.UtcNow.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "\" }," +
            "  \"Agent\": { \"Tunnel\": { \"SshExecutablePath\": \"C:\\\\Users\\\\Public\\\\ssh.exe\" }, \"EnrollmentDir\": \"C:\\\\Users\\\\Public\" }," +
            "  \"Logging\": { \"LogLevel\": { \"Default\": \"Trace\" }, \"EventLog\": { \"MachineName\": \"elsewhere\", \"LogName\": \"Other\" } } }");
        var config = new ConfigurationBuilder().Add(new DiagConfigurationSource()).Build();

        Assert.Null(config["Agent:Tunnel:SshExecutablePath"]);
        Assert.Null(config["Agent:EnrollmentDir"]);
        Assert.Null(config["Logging:EventLog:MachineName"]);
        Assert.Null(config["Logging:EventLog:LogName"]);
        Assert.Equal("Debug", config["Logging:LogLevel:Default"]);   // the fixed level, not the file's "Trace"
        Assert.Equal(
            DiagMode.LogLevelOverrides.Keys.OrderBy(k => k),
            config.AsEnumerable().Where(kv => kv.Value is not null).Select(kv => kv.Key).OrderBy(k => k),
            StringComparer.OrdinalIgnoreCase);

        // Expired or absent: nothing at all.
        File.WriteAllText(DiagMode.FilePath, "{ \"Diag\": { \"Until\": \"2020-01-01T00:00:00Z\" }, \"Agent\": { \"EnrollmentDir\": \"x\" } }");
        Assert.DoesNotContain(new ConfigurationBuilder().Add(new DiagConfigurationSource()).Build().AsEnumerable(), kv => kv.Value is not null);
    }

    [Fact]
    public void Enable_never_reuses_a_temporary_file_that_was_already_there()
    {
        var tmp = DiagMode.FilePath + ".tmp";
        File.WriteAllText(tmp, "left behind by someone else");
        DiagMode.Enable(1);
        Assert.False(File.Exists(tmp));
        Assert.DoesNotContain("left behind", File.ReadAllText(DiagMode.FilePath));
        Assert.True(DiagMode.IsActive);
    }

    [Fact]
    public void The_file_log_writes_the_servers_format_and_prunes_by_age()
    {
        var dir = DiagMode.LogDirectory;
        Directory.CreateDirectory(dir);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var old = FileLogProvider.FilePath(dir, today.AddDays(-20));
        var recent = FileLogProvider.FilePath(dir, today.AddDays(-3));
        File.WriteAllText(old, "old\n");
        File.WriteAllText(recent, "recent\n");

        using (var provider = new FileLogProvider(dir, retentionDays: 14))
        {
            Assert.Equal(dir, provider.Directory);
            var logger = provider.CreateLogger("RemoteAgent.Test");
            logger.LogDebug("debug line {N}", 1);
            logger.LogWarning(new InvalidOperationException("boom\r\nsecond line"), "something {What}", "failed");
        }   // Dispose flushes

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        var lines = File.ReadAllLines(FileLogProvider.FilePath(dir, today));
        Assert.Contains(lines, l => Regex.IsMatch(l, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z DBG RemoteAgent\.Test debug line 1$"));
        Assert.Contains(lines, l => l.EndsWith(" WRN RemoteAgent.Test something failed"));
        Assert.Contains(lines, l => l.StartsWith("    System.InvalidOperationException: boom"));
        Assert.Contains("    second line", lines);
    }

    [Fact]
    public void An_unusable_log_directory_is_reported_not_thrown()
    {
        var file = Path.Combine(_root, "a-file");
        File.WriteAllText(file, "x");
        using var provider = new FileLogProvider(Path.Combine(file, "logs"), 14);
        Assert.Null(provider.Directory);
        Assert.NotNull(provider.DirectoryError);
        provider.CreateLogger("x").LogInformation("dropped quietly");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(100);
    }

    /// <summary>Stands in for the Windows event log provider: same alias, so the same configuration rules apply.</summary>
    [ProviderAlias("EventLog")]
    private sealed class FakeEventLogProvider : ILoggerProvider
    {
        public int Debugs, Infos;
        public ILogger CreateLogger(string categoryName) => new Counter(this);
        public void Dispose() { }

        private sealed class Counter(FakeEventLogProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Debug) owner.Debugs++;
                else if (logLevel == LogLevel.Information) owner.Infos++;
            }
        }
    }
}

[CollectionDefinition("DiagMode", DisableParallelization = true)]
public class DiagModeCollection;
