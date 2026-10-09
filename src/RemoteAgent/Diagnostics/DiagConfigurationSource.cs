using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace RemoteAgent.Diagnostics;

/// <summary>
/// The configuration source behind verbose logging. It watches <c>diag.json</c> so a change takes effect
/// without a restart, but it never passes the file's content on: while <see cref="DiagMode.IsActive"/> it
/// exposes exactly the fixed log levels in <see cref="DiagMode.LogLevelOverrides"/>, otherwise nothing.
/// The file sits in a folder the service shares with the machine's users, so whatever else someone writes
/// into it - an Agent:* path, an EventLog provider setting - reaches no part of the configuration.
/// </summary>
public sealed class DiagConfigurationSource : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new DiagConfigurationProvider();
}

internal sealed class DiagConfigurationProvider : ConfigurationProvider, IDisposable
{
    // Like the JSON provider's reload delay: the writer replaces the file with a move, but a watcher event
    // can still arrive a moment before the new file is readable.
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(250);

    private readonly PhysicalFileProvider? _files;
    private readonly IDisposable? _watch;

    public DiagConfigurationProvider()
    {
        var dir = Path.GetDirectoryName(DiagMode.FilePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;   // unenrolled console run: no override
        _files = new PhysicalFileProvider(dir);
        var name = Path.GetFileName(DiagMode.FilePath);
        _watch = ChangeToken.OnChange(() => _files.Watch(name), () =>
        {
            Thread.Sleep(ReloadDelay);
            Load();
            OnReload();
        });
    }

    public override void Load() =>
        Data = DiagMode.IsActive
            ? new Dictionary<string, string?>(DiagMode.LogLevelOverrides, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        _watch?.Dispose();
        _files?.Dispose();
    }
}
