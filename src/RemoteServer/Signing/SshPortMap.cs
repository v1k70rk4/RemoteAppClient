using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RemoteServer.Configuration;
using RemoteServer.Data;

namespace RemoteServer.Signing;

/// <summary>The two bastion loopback ports a device's reverse forwards bind: its VNC port, allocated at
/// enrolment, and the file-service port derived from it.</summary>
public static class TunnelPorts
{
    public const int FilePortBase = 60000;

    /// <summary>File-service forward port for a device whose VNC port is <paramref name="vncPort"/>: deterministic,
    /// 60000-64999, distinct from the VNC range (50000-59999).</summary>
    public static int FileFor(int vncPort) => FilePortBase + ((vncPort - 50000) % 5000);
}

/// <summary>
/// Keeps a file the bastion's sshd reads to learn which loopback ports each device may listen on
/// (<c>deploy/steps/05-bastion.sh</c> installs the AuthorizedPrincipalsCommand that looks a certificate's key id
/// up in it). Every agent certificate carries the same principal, so without this any holder of a valid
/// certificate - every enrolled machine, so anyone who is SYSTEM on one - could bind another device's reverse
/// port on the bastion: hold it so that device cannot be reached, or sit a fake endpoint on it for the next
/// operator. With the map, sshd lets a device listen on its own two ports and nothing else.
///
/// One line per device, <c>deviceId TAB vncPort TAB filePort</c>, rewritten atomically whenever it changes
/// (checked once a minute, and at start). A device missing from the map is let through unrestricted by the
/// command, so a stale or absent map never locks the fleet out; it only loses the restriction.
/// </summary>
public sealed class SshPortMap(IOptions<ServerOptions> options, IServiceScopeFactory scopes, ILogger<SshPortMap> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private readonly string? _path = string.IsNullOrWhiteSpace(options.Value.Bastion.RevokedKeysPath)
        ? null
        : Path.Combine(Path.GetDirectoryName(options.Value.Bastion.RevokedKeysPath) ?? "/var/lib/remoteserver/ssh", "ports.txt");

    /// <summary>The file's content for these devices: one line each, sorted by device id, trailing newline.</summary>
    public static string Render(IEnumerable<(string DeviceId, int VncPort)> devices)
    {
        var sb = new StringBuilder();
        foreach (var (id, vnc) in devices.OrderBy(d => d.DeviceId, StringComparer.Ordinal))
            sb.Append(id).Append('\t').Append(vnc).Append('\t').Append(TunnelPorts.FileFor(vnc)).Append('\n');
        return sb.ToString();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || _path is null) return;
        while (!ct.IsCancellationRequested)
        {
            try { await RefreshAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "SSH port map not written"); }
            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Writes the map if it differs from what is on disk.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (_path is null) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var devices = await db.Devices.AsNoTracking()
            .Where(d => d.TunnelPort != null && !d.DeviceId.StartsWith("opsrc:"))
            .Select(d => new { d.DeviceId, Port = d.TunnelPort!.Value })
            .ToListAsync(ct);
        var content = Render(devices.Select(d => (d.DeviceId, d.Port)));

        string? current = null;
        try { if (File.Exists(_path)) current = await File.ReadAllTextAsync(_path, ct); } catch { /* rewrite below */ }
        if (current == content) return;

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, _path, overwrite: true);
        logger.LogInformation("SSH port map written: {Count} devices", devices.Count);
    }
}
