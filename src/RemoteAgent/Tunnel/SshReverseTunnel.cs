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
/// The bastion port is chosen by <see cref="TransportState"/>: 443 (sslh) preferred, 22 fallback.
/// </summary>
public sealed class SshReverseTunnel(TunnelOptions options, TransportState transport, ILogger logger) : IAsyncDisposable
{
    private Process? _process;
    private string? _knownHostsPath;
    private WsBridgeListener? _bridge;

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(int remotePort, CancellationToken ct)
    {
        if (IsRunning)
        {
            logger.LogInformation(L.SshReverseTunnel_TunnelAlreadyRunningSkippingNew);
            return;
        }

        foreach (var a in transport.Attempts(options.BastionPort))
        {
            var ok = a.Wss ? await TryStartWssAsync(remotePort, ct)
                           : await TryStartRawAsync(remotePort, a.Port, ct);
            if (ok) { transport.RecordWorking(a); return; }
            logger.LogWarning("Reverse tunnel attempt failed: {Attempt}.", a.Wss ? "WSS" : $"port {a.Port}");
        }

        CleanupKnownHosts();
        if (_bridge is not null) { await _bridge.DisposeAsync(); _bridge = null; }
    }

    // Raw SSH on a bastion port (443 sslh mux, or 22).
    private async Task<bool> TryStartRawAsync(int remotePort, int port, CancellationToken ct)
    {
        CleanupKnownHosts();
        _knownHostsPath = WritePinnedKnownHosts();
        if (!await TryStartOnPortAsync(remotePort, port, options.BastionHost, ct)) return false;
        logger.LogInformation(L.SshReverseTunnel_StartingReverseTunnelBastionHost,
            options.BastionHost, port, remotePort, options.LocalForwardPort);
        return true;
    }

    // SSH-over-WebSocket: ssh reaches the bastion through a local TCP→WSS bridge to the server's
    // /ssh endpoint (DPI / Cloudflare friendly).
    private async Task<bool> TryStartWssAsync(int remotePort, CancellationToken ct)
    {
        var (url, pfx, thumb, pin) = transport.WssParams;
        if (string.IsNullOrWhiteSpace(url)) { logger.LogWarning("WSS attempt: no /ssh URL configured."); return false; }
        CleanupKnownHosts();
        _bridge = new WsBridgeListener(url, pfx, thumb, pin, logger);
        var bport = _bridge.Start();
        _knownHostsPath = WriteKnownHosts("127.0.0.1", bport);
        if (await TryStartOnPortAsync(remotePort, bport, "127.0.0.1", ct))
        {
            logger.LogInformation("Reverse tunnel up over WSS ({Url}, remote {Remote}).", url, remotePort);
            return true;
        }
        await _bridge.DisposeAsync(); _bridge = null;
        return false;
    }

    private async Task<bool> TryStartOnPortAsync(int remotePort, int port, string host, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveSshPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        // -N: only the forward, no remote command. -R remote:127.0.0.1:local — a reverse forward.
        // 127.0.0.1 instead of 'localhost': Windows ssh may resolve localhost to ::1, while VNC
        // typically listens only on IPv4 loopback.
        psi.ArgumentList.Add("-N");
        psi.ArgumentList.Add("-R");
        psi.ArgumentList.Add($"{remotePort}:127.0.0.1:{options.LocalForwardPort}");

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(options.PrivateKeyPath);
        if (!string.IsNullOrWhiteSpace(options.CertificatePath))
            AddOption(psi, $"CertificateFile=\"{options.CertificatePath}\"");

        AddOption(psi, "IdentitiesOnly=yes");
        AddOption(psi, "BatchMode=yes");                       // no prompts
        AddOption(psi, "StrictHostKeyChecking=yes");           // fail on unknown keys
        AddOption(psi, $"UserKnownHostsFile=\"{_knownHostsPath}\"");
        AddOption(psi, "ExitOnForwardFailure=yes");            // exit if the remote forward is refused
        AddOption(psi, "ConnectTimeout=8");                    // bound a dead port so fallback is quick
        AddOption(psi, "ServerAliveInterval=15");              // keepalive
        AddOption(psi, "ServerAliveCountMax=3");
        psi.ArgumentList.Add("-v");                            // verbose: lets us detect auth and fail over

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add($"{options.BastionUser}@{host}");

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var authed = false;
        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            if (e.Data.Contains("Authenticat", StringComparison.OrdinalIgnoreCase)) authed = true;
            // ssh -v debug lines start with "debug"; log only real warnings/errors to keep the EventLog clean.
            if (!e.Data.StartsWith("debug", StringComparison.OrdinalIgnoreCase))
                logger.LogWarning("ssh: {Line}", e.Data);
        };
        proc.Start();
        proc.BeginErrorReadLine();
        _process = proc;

        // Wait for authentication or a quick failure (ConnectTimeout=8 bounds a dead/black-holed port).
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline && !authed && !proc.HasExited)
        {
            try { await Task.Delay(150, ct); } catch { break; }
        }
        if (!authed || proc.HasExited) { await KillQuietly(proc); return false; }

        // Authenticated. ExitOnForwardFailure makes ssh exit promptly if the -R forward was refused;
        // if it is still alive after a short grace, the forward is up.
        try { await Task.Delay(2000, ct); } catch { }
        if (proc.HasExited) { await KillQuietly(proc); return false; }
        return true;
    }

    public async Task StopAsync()
    {
        var proc = _process;
        if (proc is not null)
        {
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
            }
        }
        if (_bridge is not null) { await _bridge.DisposeAsync(); _bridge = null; }
        CleanupKnownHosts();
        if (proc is not null) logger.LogInformation("Reverse tunnel stopped.");
    }

    private async Task KillQuietly(Process proc)
    {
        try { if (!proc.HasExited) { proc.Kill(entireProcessTree: true); await proc.WaitForExitAsync(); } } catch { /* best effort */ }
        try { proc.Dispose(); } catch { /* best effort */ }
        if (ReferenceEquals(_process, proc)) _process = null;
    }

    private string WritePinnedKnownHosts()
    {
        // Same bastion host key on every port; write an entry for each candidate so ssh matches
        // regardless of the port we connect on. "[host]:port" is required for non-default ports.
        var path = Path.Combine(Path.GetTempPath(), $"ra_known_hosts_{Guid.NewGuid():N}");
        var lines = new List<string>
        {
            $"{options.BastionHost} {options.BastionHostKey}",
            $"[{options.BastionHost}]:443 {options.BastionHostKey}",
        };
        if (options.BastionPort != 22 && options.BastionPort != 443)
            lines.Add($"[{options.BastionHost}]:{options.BastionPort} {options.BastionHostKey}");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private string WriteKnownHosts(string host, int port)
    {
        // Single pinned entry for the wss443 bridge: ssh connects to [127.0.0.1]:<bridgePort>, but the
        // host key it sees is the bastion sshd's (through the WS), so pin that key under this address.
        var path = Path.Combine(Path.GetTempPath(), $"ra_known_hosts_{Guid.NewGuid():N}");
        var entry = port == 22 ? host : $"[{host}]:{port}";
        File.WriteAllText(path, $"{entry} {options.BastionHostKey}\n");
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
