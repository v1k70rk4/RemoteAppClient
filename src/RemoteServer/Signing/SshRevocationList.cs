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

    /// <summary>Adds a deleted device's SSH public key to the list and rebuilds the KRL. Never throws.</summary>
    public async Task RevokeAsync(string? sshPublicKey, string deviceId, string? hostname, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(_krlPath)) return;
        var key = sshPublicKey?.Trim();
        if (string.IsNullOrEmpty(key) || key.Contains('\n') || key.Contains('\r')) return;

        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_krlPath)!);
            var label = new string((hostname ?? "").Where(c => !char.IsControl(c)).ToArray());
            await File.AppendAllTextAsync(ListPath,
                $"# {DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'} {deviceId} {label}\n{key}\n", ct);
            await RebuildAsync(ct);
        }
        catch (Exception ex) { logger.LogWarning(ex, L.SshRevocationList_UpdateFailed, deviceId); }
        finally { _gate.Release(); }
    }

    // ssh-keygen -k -f <tmp> <list>: the whole list each time, so the KRL always matches it exactly.
    private async Task RebuildAsync(CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(_krlPath)!;
            Directory.CreateDirectory(dir);
            if (!File.Exists(ListPath)) await File.WriteAllTextAsync(ListPath, "", ct);

            var tmp = _krlPath + ".tmp";
            File.Delete(tmp);
            var psi = new ProcessStartInfo("ssh-keygen") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            psi.ArgumentList.Add("-k");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add(ListPath);
            using var proc = Process.Start(psi)!;
            var err = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0 || !File.Exists(tmp))
            {
                logger.LogWarning(L.SshRevocationList_BuildFailed, err.Trim());
                return;   // the previous KRL stays in place
            }

            // sshd runs as root and only reads it; world-readable so nothing about ownership can lock it out.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.Move(tmp, _krlPath, overwrite: true);
            logger.LogInformation(L.SshRevocationList_Rebuilt, _krlPath, CountKeys());
        }
        catch (Exception ex) { logger.LogWarning(ex, L.SshRevocationList_BuildFailed, ex.Message); }
    }

    private int CountKeys()
    {
        try { return File.ReadLines(ListPath).Count(l => l.Length > 0 && !l.StartsWith('#')); }
        catch { return 0; }
    }
}
