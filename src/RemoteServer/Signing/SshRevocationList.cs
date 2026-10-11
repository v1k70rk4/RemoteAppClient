using System.Diagnostics;
using Microsoft.Extensions.Options;
using RemoteServer.Configuration;
using L = RemoteServer.Localization.Strings;

namespace RemoteServer.Signing;

/// <summary>
/// The bastion's revocation list (OpenSSH KRL) for deleted devices. Deleting a device revokes it on the server;
/// this makes its SSH key - and with it every certificate issued for that key - useless at the bastion as well,
/// without touching any other device and without certificates having to expire.
///
/// The source is a plain text file of revoked public keys (one per line, a comment line above each saying which
/// device it was); the KRL is rebuilt from it with <c>ssh-keygen -k</c> into a temporary file and moved into
/// place, so sshd never sees a missing or half-written list. The KRL is created at start, empty if need be:
/// sshd refuses every key when a configured RevokedKeys file is missing, so it has to exist before sshd is
/// pointed at it. Linux only (ssh-keygen); elsewhere it does nothing.
/// </summary>
public sealed class SshRevocationList(IOptions<ServerOptions> options, ILogger<SshRevocationList> logger) : IHostedService
{
    private readonly string _krlPath = options.Value.Bastion.RevokedKeysPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string ListPath => Path.ChangeExtension(_krlPath, ".txt");

    public async Task StartAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(_krlPath)) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_krlPath) || (File.Exists(ListPath) && File.GetLastWriteTimeUtc(ListPath) > File.GetLastWriteTimeUtc(_krlPath)))
                await RebuildAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Puts a device's SSH public key on the list and rebuilds the KRL, all or nothing: the key is appended to a
    /// copy of the list, the KRL is built from that copy, and only when both exist are they moved into place. A
    /// failure anywhere leaves list and KRL as they were, so the caller can trust the answer: true means sshd
    /// refuses the key from now on; false means it is still accepted and nothing has changed. Never throws.
    /// Where there is no list (not Linux, no path) there is nothing to revoke at and the answer is true.
    /// </summary>
    public async Task<bool> RevokeAsync(string? sshPublicKey, string deviceId, string? hostname, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(_krlPath)) return true;
        var key = sshPublicKey?.Trim();
        if (string.IsNullOrEmpty(key) || key.Contains('\n') || key.Contains('\r')) return true; // nothing to revoke

        await _gate.WaitAsync(ct);
        try
        {
            var dir = Path.GetDirectoryName(_krlPath)!;
            Directory.CreateDirectory(dir);
            var current = File.Exists(ListPath) ? await File.ReadAllTextAsync(ListPath, ct) : "";
            if (KeyBody(key) is { } body && current.Split('\n').Any(l => !l.StartsWith('#') && KeyBody(l) == body))
            {
                // Already on the list. The KRL in place carries it unless an earlier update stopped between the
                // two moves below (list published, KRL not): then the list is the newer file and the KRL is rebuilt
                // from it now, the same repair start-up makes.
                if (File.Exists(_krlPath) && File.GetLastWriteTimeUtc(ListPath) <= File.GetLastWriteTimeUtc(_krlPath)) return true;
                return await RebuildAsync(ct);
            }

            var label = new string((hostname ?? "").Where(c => !char.IsControl(c)).ToArray());
            var listTmp = ListPath + ".tmp";
            await File.WriteAllTextAsync(listTmp, current + (current.Length > 0 && !current.EndsWith('\n') ? "\n" : "")
                + $"# {DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'} {deviceId} {label}\n{key}\n", ct);
            var krlTmp = _krlPath + ".tmp";
            if (!await BuildAsync(listTmp, krlTmp, ct)) { File.Delete(listTmp); return false; }
            // The list is the source of truth and goes first: a stop between the two moves leaves a list newer
            // than the KRL, which start-up (and the branch above) repairs by rebuilding the KRL from it. The other
            // order could publish a KRL whose key a later rebuild from the older list would silently drop.
            File.Move(listTmp, ListPath, overwrite: true);
            File.Move(krlTmp, _krlPath, overwrite: true);
            logger.LogInformation(L.SshRevocationList_Rebuilt, _krlPath, CountKeys());
            return true;
        }
        catch (Exception ex) { logger.LogWarning(ex, L.SshRevocationList_UpdateFailed, deviceId); return false; }
        finally { _gate.Release(); }
    }

    /// <summary>Whether this public key is on the list. A revoked key is never adopted again, so a rotation that
    /// went wrong halfway cannot bring a revoked key back.</summary>
    public async Task<bool> IsRevokedAsync(string sshPublicKey, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(_krlPath) || !File.Exists(ListPath)) return false;
        var wanted = KeyBody(sshPublicKey);
        if (wanted is null) return false;
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var line in await File.ReadAllLinesAsync(ListPath, ct))
                if (!line.StartsWith('#') && KeyBody(line) == wanted) return true;
            return false;
        }
        finally { _gate.Release(); }
    }

    /// <summary>"type base64" without the comment, so the same key matches whatever it was labelled.</summary>
    public static string? KeyBody(string? line)
    {
        var parts = (line ?? "").Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[0] + " " + parts[1] : null;
    }

    // The KRL in place is rebuilt from the list in place (start-up, when the list is newer than the KRL).
    // True when the KRL in place now matches the list.
    private async Task<bool> RebuildAsync(CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(_krlPath)!;
            Directory.CreateDirectory(dir);
            if (!File.Exists(ListPath)) await File.WriteAllTextAsync(ListPath, "", ct);
            var tmp = _krlPath + ".tmp";
            if (!await BuildAsync(ListPath, tmp, ct)) return false; // the previous KRL stays in place
            File.Move(tmp, _krlPath, overwrite: true);
            logger.LogInformation(L.SshRevocationList_Rebuilt, _krlPath, CountKeys());
            return true;
        }
        catch (Exception ex) { logger.LogWarning(ex, L.SshRevocationList_BuildFailed, ex.Message); return false; }
    }

    /// <summary>ssh-keygen -k -f &lt;krl&gt; &lt;list&gt;: the whole list each time, so the KRL always matches it exactly.
    /// True when the KRL file exists afterwards.</summary>
    private async Task<bool> BuildAsync(string listPath, string krlOut, CancellationToken ct)
    {
        File.Delete(krlOut);
        var psi = new ProcessStartInfo("ssh-keygen") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-k");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(krlOut);
        psi.ArgumentList.Add(listPath);
        using var proc = Process.Start(psi)!;
        var err = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0 || !File.Exists(krlOut))
        {
            logger.LogWarning(L.SshRevocationList_BuildFailed, err.Trim());
            return false;
        }
        // sshd runs as root and only reads it; world-readable so nothing about ownership can lock it out.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(krlOut, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return true;
    }

    private int CountKeys()
    {
        try { return File.ReadLines(ListPath).Count(l => l.Length > 0 && !l.StartsWith('#')); }
        catch { return 0; }
    }
}
