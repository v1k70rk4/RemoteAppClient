using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;
using RemoteServer.Configuration;

namespace RemoteServer.Signing;

/// <summary>
/// Which devices may bring a reverse tunnel up on the bastion right now, and on which ports. Every agent
/// certificate carries the same principal, so sshd alone cannot tell one device's -R from another's: any
/// enrolled machine (anyone who is SYSTEM on one) could bind another device's port - hold it so that device
/// cannot be reached, or sit a fake endpoint on it for the next operator. Now a device may listen only on its
/// own two ports, and only within <see cref="Window"/> of an operator asking for it: the open-tunnel command's
/// delivery is the request, and it is written here for the bastion's AuthorizedPrincipalsCommand
/// (deploy/steps/05-bastion.sh) to read. The agent brings its tunnel up within seconds of the command, and a
/// tunnel that drops is not rebuilt by the agent - the operator asks again, which grants again.
///
/// Each port carries its own time: a file session asked for while a VNC request still waits for the person at
/// the device keeps the VNC port in the grant, but on the VNC request's own clock - a file request never
/// extends a VNC permission. The file is rewritten the moment a grant is made and whenever one runs out, one
/// line per device: <c>deviceId TAB vncPort TAB filePort TAB vncGrantedAt TAB fileGrantedAt</c> (unix seconds,
/// 0 for a port not granted). The bastion checks the ages itself too, so a file left behind by a stopped server
/// grants nothing.
/// </summary>
public sealed class SshTunnelGrants(IOptions<ServerOptions> options, ILogger<SshTunnelGrants> logger) : BackgroundService
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Sweep = TimeSpan.FromSeconds(15);

    /// <summary>The grants file's format. The bastion script that reads it names the format it understands
    /// ("rac-principals format N" in deploy/steps/05-bastion.sh); a server update brings a new file format
    /// without touching the box's script, so the server checks the two agree and says so when they do not.</summary>
    public const int FileFormat = 2;
    public const string PrincipalsScript = "/usr/local/sbin/rac-principals";

    /// <summary>The format the installed bastion script reads: null when none is installed (the bastion step
    /// predates listen rights, or this is not the bastion), 1 for a script from before formats were named.</summary>
    public static int? InstalledScriptFormat(string path = PrincipalsScript)
    {
        try { return File.Exists(path) ? ScriptFormatOf(File.ReadAllText(path)) : null; }
        catch (Exception) { return null; }
    }

    public static int ScriptFormatOf(string script)
    {
        var m = System.Text.RegularExpressions.Regex.Match(script, @"rac-principals format (\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) return n;
        // Before the marker: the format-2 script (per-port times) already read the fifth column, the first one
        // did not. A box whose step ran between the two is not sent to rerun it for a line of comment.
        return script.Contains("$2, $3, $4, $5", StringComparison.Ordinal) ? 2 : 1;
    }

    /// <summary>A port of 0 is not granted; its time is then irrelevant.</summary>
    public sealed record Grant(int VncPort, int FilePort, DateTimeOffset VncAt, DateTimeOffset FileAt)
    {
        public bool VncLive(DateTimeOffset now) => VncPort > 0 && now - VncAt < Window;
        public bool FileLive(DateTimeOffset now) => FilePort > 0 && now - FileAt < Window;
        public bool AnyLive(DateTimeOffset now) => VncLive(now) || FileLive(now);
    }

    private readonly ConcurrentDictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly string? _path = string.IsNullOrWhiteSpace(options.Value.Bastion.RevokedKeysPath)
        ? null
        : Path.Combine(Path.GetDirectoryName(options.Value.Bastion.RevokedKeysPath) ?? "/var/lib/remoteserver/ssh", "tunnel-grants.txt");

    /// <summary>Lets the device bring its reverse tunnel up on these ports (0 = not this one) for the next
    /// <see cref="Window"/>; a port a standing grant still holds keeps its own time.</summary>
    public async Task GrantAsync(string deviceId, int vncPort, int filePort, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        _grants.AddOrUpdate(deviceId, Merge(null, vncPort, filePort, now), (_, prior) => Merge(prior, vncPort, filePort, now));
        await WriteAsync(ct);
    }

    /// <summary>What a new grant becomes next to a standing one: a port asked for now gets the time now; a port
    /// not asked for now is carried over from the standing grant with its own time, while that is still live;
    /// anything else is not granted.</summary>
    public static Grant Merge(Grant? prior, int vncPort, int filePort, DateTimeOffset now)
    {
        var vnc = vncPort > 0 ? (vncPort, now)
                : prior is not null && prior.VncLive(now) ? (prior.VncPort, prior.VncAt)
                : (0, DateTimeOffset.MinValue);
        var file = filePort > 0 ? (filePort, now)
                 : prior is not null && prior.FileLive(now) ? (prior.FilePort, prior.FileAt)
                 : (0, DateTimeOffset.MinValue);
        return new Grant(vnc.Item1, file.Item1, vnc.Item2, file.Item2);
    }

    /// <summary>The grants with a live port at <paramref name="now"/>, as the file's content: one line per device
    /// sorted by id, a port that has run out shown as 0, trailing newline, empty when none.</summary>
    public static string Render(IEnumerable<KeyValuePair<string, Grant>> grants, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        foreach (var (id, g) in grants.Where(kv => kv.Value.AnyLive(now)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var (vnc, vncAt) = g.VncLive(now) ? (g.VncPort, g.VncAt.ToUnixTimeSeconds()) : (0, 0L);
            var (file, fileAt) = g.FileLive(now) ? (g.FilePort, g.FileAt.ToUnixTimeSeconds()) : (0, 0L);
            sb.Append(id).Append('\t').Append(vnc).Append('\t').Append(file).Append('\t').Append(vncAt).Append('\t').Append(fileAt).Append('\n');
        }
        return sb.ToString();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || _path is null) return;
        if (InstalledScriptFormat() is { } installed && installed != FileFormat)
            logger.LogWarning("The bastion's {Script} reads tunnel-grants format {Installed}, this server writes format {Current}: rerun ./deploy/setup.sh 05-bastion on the box, or a device's tunnel is turned away under enforce", PrincipalsScript, installed, FileFormat);
        try { await WriteAsync(ct); } catch (Exception ex) { logger.LogWarning(ex, "Tunnel grants not written at start"); }
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Sweep, ct); } catch (OperationCanceledException) { return; }
            var now = DateTimeOffset.UtcNow;
            bool expired = false;
            foreach (var (id, g) in _grants)
                if (!g.AnyLive(now) && _grants.TryRemove(new KeyValuePair<string, Grant>(id, g))) expired = true;
            if (expired)
            {
                try { await WriteAsync(ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { logger.LogWarning(ex, "Tunnel grants not written"); }
            }
        }
    }

    private async Task WriteAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || _path is null) return;
        await _write.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, Render(_grants, DateTimeOffset.UtcNow), ct);
            // The bastion script reads the file as "nobody": world-readable whatever this process's umask makes
            // of a new file (a strict umask gave it 0600, and every device was then turned away under enforce).
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.Move(tmp, _path, overwrite: true);
        }
        finally { _write.Release(); }
    }
}
