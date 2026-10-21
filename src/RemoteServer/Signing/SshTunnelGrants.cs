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
/// The file is rewritten the moment a grant is made and whenever one runs out, one line per device:
/// <c>deviceId TAB vncPort TAB filePort TAB grantedAtUnixSeconds</c>. The bastion checks the age itself too, so
/// a file left behind by a stopped server grants nothing.
/// </summary>
public sealed class SshTunnelGrants(IOptions<ServerOptions> options, ILogger<SshTunnelGrants> logger) : BackgroundService
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Sweep = TimeSpan.FromSeconds(15);

    public sealed record Grant(int VncPort, int FilePort, DateTimeOffset At);

    private readonly ConcurrentDictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly string? _path = string.IsNullOrWhiteSpace(options.Value.Bastion.RevokedKeysPath)
        ? null
        : Path.Combine(Path.GetDirectoryName(options.Value.Bastion.RevokedKeysPath) ?? "/var/lib/remoteserver/ssh", "tunnel-grants.txt");

    /// <summary>Lets the device bring its reverse tunnel up for the next <see cref="Window"/>.</summary>
    public async Task GrantAsync(string deviceId, int vncPort, int filePort, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // One grant per device: a file session's grant (file port alone) must not strip the VNC port from a VNC
        // request still waiting for the person at the device to answer - another operator opening files in those
        // seconds would leave the approved VNC session unable to come up. A port the standing grant still holds
        // is carried over.
        _grants.AddOrUpdate(deviceId, Merge(null, vncPort, filePort, now), (_, prior) => Merge(prior, vncPort, filePort, now));
        await WriteAsync(ct);
    }

    /// <summary>What a new grant becomes next to a standing one (pure, for the tests).</summary>
    public static Grant Merge(Grant? prior, int vncPort, int filePort, DateTimeOffset now) =>
        prior is not null && now - prior.At < Window
            ? new Grant(vncPort > 0 ? vncPort : prior.VncPort, filePort > 0 ? filePort : prior.FilePort, now)
            : new Grant(vncPort, filePort, now);

    /// <summary>The grants still inside the window at <paramref name="now"/>, as the file's content: one line per
    /// device sorted by id, trailing newline, empty when none.</summary>
    public static string Render(IEnumerable<KeyValuePair<string, Grant>> grants, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        foreach (var (id, g) in grants.Where(kv => now - kv.Value.At < Window).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(id).Append('\t').Append(g.VncPort).Append('\t').Append(g.FilePort).Append('\t').Append(g.At.ToUnixTimeSeconds()).Append('\n');
        return sb.ToString();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || _path is null) return;
        try { await WriteAsync(ct); } catch (Exception ex) { logger.LogWarning(ex, "Tunnel grants not written at start"); }
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Sweep, ct); } catch (OperationCanceledException) { return; }
            var now = DateTimeOffset.UtcNow;
            bool expired = false;
            foreach (var (id, g) in _grants)
                if (now - g.At >= Window && _grants.TryRemove(new KeyValuePair<string, Grant>(id, g))) expired = true;
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
            File.Move(tmp, _path, overwrite: true);
        }
        finally { _write.Release(); }
    }
}
