using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RemoteAgent.Configuration;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Tunnel;

/// <summary>
/// Single LOCAL forward (ssh -L) to the bastion using the device enrollment key. The console
/// broker opens it on client request: a local loopback port points to bastion
/// 127.0.0.1:&lt;remotePort&gt; for the admin API or a target device VNC bastion port.
/// The bastion host key is pinned, so the connection can only be built to our server.
/// The bastion port is chosen by <see cref="TransportState"/>: 443 (sslh) preferred, 22 fallback.
/// </summary>
public sealed class SshLocalForward(TunnelOptions options, TransportState transport, ILogger logger) : IAsyncDisposable
{
    private Process? _process;
    private string? _knownHostsPath;
    private WsBridgeListener? _bridge;

    public int LocalPort { get; private set; }
    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(int remotePort, CancellationToken ct)
    {
        foreach (var a in transport.Attempts(options.BastionPort))
        {
            var ok = a.Wss ? await TryStartWssAsync(remotePort, ct)
                           : await TryStartRawAsync(remotePort, a.Port, ct);
            if (ok) { transport.RecordWorking(a); return; }
            logger.LogWarning("Broker forward attempt failed: {Attempt}.", a.Wss ? "WSS" : $"port {a.Port}");
        }
        CleanupKnownHosts();
        if (_bridge is not null) { await _bridge.DisposeAsync(); _bridge = null; }
        throw new InvalidOperationException(L.SshLocalForward_SshLForwardWasNot);
    }

    // Raw SSH on a bastion port (443 sslh mux, or 22).
    private async Task<bool> TryStartRawAsync(int remotePort, int port, CancellationToken ct)
    {
        CleanupKnownHosts();
        _knownHostsPath = WritePinnedKnownHosts();
        return await TryStartOnPortAsync(remotePort, port, options.BastionHost, ct);
    }

    // SSH-over-WebSocket: the broker reaches the bastion through a local TCP→WSS bridge to /ssh.
    private async Task<bool> TryStartWssAsync(int remotePort, CancellationToken ct)
    {
        var (url, pfx, thumb, pin) = transport.WssParams;
        if (string.IsNullOrWhiteSpace(url)) { logger.LogWarning("WSS attempt: no /ssh URL configured."); return false; }
        CleanupKnownHosts();
        _bridge = new WsBridgeListener(url, pfx, thumb, pin, logger);
        var bport = _bridge.Start();
        _knownHostsPath = WriteKnownHosts("127.0.0.1", bport);
        if (await TryStartOnPortAsync(remotePort, bport, "127.0.0.1", ct)) return true;
        await _bridge.DisposeAsync(); _bridge = null;
        return false;
    }

    private async Task<bool> TryStartOnPortAsync(int remotePort, int port, string host, CancellationToken ct)
    {
        LocalPort = FreeLoopbackPort();

        var psi = new ProcessStartInfo
        {
            FileName = ResolveSshPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        // -N: forward only; -L local:127.0.0.1:remote points to bastion loopback.
        psi.ArgumentList.Add("-N");
        psi.ArgumentList.Add("-L");
        psi.ArgumentList.Add($"127.0.0.1:{LocalPort}:127.0.0.1:{remotePort}");

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(options.PrivateKeyPath);
        if (!string.IsNullOrWhiteSpace(options.CertificatePath))
            AddOption(psi, $"CertificateFile=\"{options.CertificatePath}\"");

        AddOption(psi, "IdentitiesOnly=yes");
        AddOption(psi, "BatchMode=yes");
        AddOption(psi, "StrictHostKeyChecking=yes");
        AddOption(psi, $"UserKnownHostsFile=\"{_knownHostsPath}\"");
        AddOption(psi, "ExitOnForwardFailure=yes");
        AddOption(psi, "ConnectTimeout=8"); // bound a dead port so fallback is quick
        AddOption(psi, "ServerAliveInterval=15");
        AddOption(psi, "ServerAliveCountMax=3");

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add($"{options.BastionUser}@{host}");

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) logger.LogWarning("ssh -L: {Line}", e.Data); };
        proc.Start();
        proc.BeginErrorReadLine();
        _process = proc;

        // Wait until ssh is up and has bound the local port, or it exits if the forward failed
        // (ExitOnForwardFailure=yes / ConnectTimeout). Poll the local port and return as soon as it accepts.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (proc.HasExited) { await KillQuietly(proc); return false; }
            if (PortAccepts(LocalPort)) return true;
            try { await Task.Delay(150, ct); } catch { break; }
        }
        if (proc.HasExited) { await KillQuietly(proc); return false; }
        return true; // alive but slow; assume ready, the client will retry if needed
    }

    private static bool PortAccepts(int port)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var ar = s.BeginConnect(IPAddress.Loopback, port, null, null);
            if (ar.AsyncWaitHandle.WaitOne(200) && s.Connected) { s.EndConnect(ar); return true; }
            return false;
        }
        catch { return false; }
    }

    public async Task StopAsync()
    {
        var proc = _process;
        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited) { proc.Kill(entireProcessTree: true); await proc.WaitForExitAsync(); }
            }
            catch (Exception ex) { logger.LogWarning(ex, L.SshLocalForward_ErrorWhileStoppingForward); }
            finally { proc.Dispose(); _process = null; }
        }
        if (_bridge is not null) { await _bridge.DisposeAsync(); _bridge = null; }
        CleanupKnownHosts();
    }

    private async Task KillQuietly(Process proc)
    {
        try { if (!proc.HasExited) { proc.Kill(entireProcessTree: true); await proc.WaitForExitAsync(); } } catch { /* best effort */ }
        try { proc.Dispose(); } catch { /* best effort */ }
        if (ReferenceEquals(_process, proc)) _process = null;
    }

    private static int FreeLoopbackPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private string WritePinnedKnownHosts()
    {
        // Same bastion host key on every port; write an entry for each candidate so ssh matches
        // regardless of the port we connect on. "[host]:port" is required for non-default ports.
        var path = Path.Combine(Path.GetTempPath(), $"ra_brk_known_{Guid.NewGuid():N}");
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
        var path = Path.Combine(Path.GetTempPath(), $"ra_brk_known_{Guid.NewGuid():N}");
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
