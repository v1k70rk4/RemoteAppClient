using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RemoteAgent.Configuration;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Tunnel;

/// <summary>
/// Lifecycle of a single reverse SSH tunnel around <c>ssh.exe</c>.
/// The bastion host key is pinned through a private known_hosts file, so the agent can
/// connect only to our server, not to an intermediary. The forward target is fixed
/// (localhost:LocalForwardPort); the server can influence only the remote port number.
/// </summary>
public sealed class SshReverseTunnel(TunnelOptions options, ILogger logger) : IAsyncDisposable
{
    private Process? _process;
    private string? _knownHostsPath;

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(int remotePort, CancellationToken ct)
    {
        if (IsRunning)
        {
            logger.LogInformation(L.SshReverseTunnel_TunnelAlreadyRunningSkippingNew);
            return;
        }

        _knownHostsPath = WritePinnedKnownHosts();

        var psi = new ProcessStartInfo
        {
            FileName = ResolveSshPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        // -N: do not run a remote command, only the forward.
        // -R remote:localhost:local — a reverse forward.
        psi.ArgumentList.Add("-N");
        psi.ArgumentList.Add("-R");
        // 127.0.0.1 instead of 'localhost': Windows ssh may resolve localhost to ::1,
        // while VNC typically listens only on IPv4 loopback.
        psi.ArgumentList.Add($"{remotePort}:127.0.0.1:{options.LocalForwardPort}");

        // Use only the configured key, without password or interactive fallback.
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(options.PrivateKeyPath);

        // SSH certificate signed by the CA trusted by bastion TrustedUserCAKeys.
        if (!string.IsNullOrWhiteSpace(options.CertificatePath))
            AddOption(psi, $"CertificateFile=\"{options.CertificatePath}\"");

        AddOption(psi, "IdentitiesOnly=yes");
        AddOption(psi, "BatchMode=yes");                       // no prompts
        AddOption(psi, "StrictHostKeyChecking=yes");           // fail on unknown keys
        AddOption(psi, $"UserKnownHostsFile=\"{_knownHostsPath}\"");
        AddOption(psi, "ExitOnForwardFailure=yes");            // exit if forward creation fails
        AddOption(psi, "ServerAliveInterval=15");              // keepalive
        AddOption(psi, "ServerAliveCountMax=3");

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(options.BastionPort.ToString());
        psi.ArgumentList.Add($"{options.BastionUser}@{options.BastionHost}");

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // ssh writes only errors/warnings to stderr without -v; log them as warnings so tunnel
        // failures are visible in the SYSTEM service EventLog.
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                logger.LogWarning("ssh: {Line}", e.Data);
        };

        logger.LogInformation(
            L.SshReverseTunnel_StartingReverseTunnelBastionHost,
            options.BastionHost, options.BastionPort, remotePort, options.LocalForwardPort);

        proc.Start();
        proc.BeginErrorReadLine();
        _process = proc;
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var proc = _process;
        if (proc is null)
            return;

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                await proc.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, L.SshReverseTunnel_ErrorWhileStoppingTunnel);
        }
        finally
        {
            proc.Dispose();
            _process = null;
            CleanupKnownHosts();
            logger.LogInformation("Reverse tunnel stopped.");
        }
    }

    private string WritePinnedKnownHosts()
    {
        // Write the pinned host key to a temporary known_hosts file for this session only.
        // Content format: "<host> <key>".
        var path = Path.Combine(Path.GetTempPath(), $"ra_known_hosts_{Guid.NewGuid():N}");
        // On port 22, ssh looks up the plain host name in known_hosts; [host]:port is only
        // valid for non-default ports.
        var hostEntry = options.BastionPort == 22
            ? options.BastionHost
            : $"[{options.BastionHost}]:{options.BastionPort}";
        File.WriteAllText(path, $"{hostEntry} {options.BastionHostKey}\n");
        return path;
    }

    private void CleanupKnownHosts()
    {
        if (_knownHostsPath is null) return;
        try { File.Delete(_knownHostsPath); } catch { /* best effort */ }
        _knownHostsPath = null;
    }

    private string ResolveSshPath() => SshTools.ResolveSsh(options.SshExecutablePath);

    private static void AddOption(ProcessStartInfo psi, string kv)
    {
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(kv);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
