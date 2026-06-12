using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RemoteAgent.Configuration;

namespace RemoteAgent.Tunnel;

/// <summary>
/// Egyetlen LOCAL forward (ssh -L) a bástyához, a GÉP enrollment-kulcsával. A konzol-bróker
/// használja: a kliens kérésére nyit egy helyi loopback-portot, ami a bástya 127.0.0.1:&lt;remotePort&gt;-jára
/// mutat (admin API vagy egy cél-gép VNC bástya-portja). A bástya host-kulcsa pinnelt, így
/// kizárólag a mi szerverünkhöz épül a kapcsolat.
/// </summary>
public sealed class SshLocalForward(TunnelOptions options, ILogger logger) : IAsyncDisposable
{
    private Process? _process;
    private string? _knownHostsPath;

    public int LocalPort { get; private set; }
    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(int remotePort, CancellationToken ct)
    {
        LocalPort = FreeLoopbackPort();
        _knownHostsPath = WritePinnedKnownHosts();

        var psi = new ProcessStartInfo
        {
            FileName = ResolveSshPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        // -N: csak forward; -L helyi:127.0.0.1:távoli — a bástya loopbackjára mutatva.
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
        AddOption(psi, "ServerAliveInterval=15");
        AddOption(psi, "ServerAliveCountMax=3");

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(options.BastionPort.ToString());
        psi.ArgumentList.Add($"{options.BastionUser}@{options.BastionHost}");

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) logger.LogWarning("ssh -L: {Line}", e.Data); };
        proc.Start();
        proc.BeginErrorReadLine();
        _process = proc;

        // Megvárjuk, míg az ssh felépül és bindeli a helyi portot (vagy kilép, ha a
        // forward nem jött létre — ExitOnForwardFailure=yes). A helyi portot pollozzuk:
        // amint elfogad kapcsolatot, kész; siker esetén gyorsan visszatér.
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (proc.HasExited)
                throw new InvalidOperationException("Az ssh -L forward nem jött létre (lásd az 'ssh -L:' sorokat a logban).");
            if (PortAccepts(LocalPort))
                return;
            await Task.Delay(150, ct);
        }
        if (proc.HasExited)
            throw new InvalidOperationException("Az ssh -L forward nem jött létre (timeout).");
        // Időtúllépés, de az ssh él — feltételezzük, hogy kész (a kliens úgyis újrapróbál).
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
        if (proc is null) return;
        try
        {
            if (!proc.HasExited) { proc.Kill(entireProcessTree: true); await proc.WaitForExitAsync(); }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Forward leállításakor hiba."); }
        finally
        {
            proc.Dispose();
            _process = null;
            CleanupKnownHosts();
        }
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
        var path = Path.Combine(Path.GetTempPath(), $"ra_brk_known_{Guid.NewGuid():N}");
        var hostEntry = options.BastionPort == 22 ? options.BastionHost : $"[{options.BastionHost}]:{options.BastionPort}";
        File.WriteAllText(path, $"{hostEntry} {options.BastionHostKey}\n");
        return path;
    }

    private void CleanupKnownHosts()
    {
        if (_knownHostsPath is null) return;
        try { File.Delete(_knownHostsPath); } catch { /* best effort */ }
        _knownHostsPath = null;
    }

    private string ResolveSshPath() =>
        string.IsNullOrWhiteSpace(options.SshExecutablePath) ? "ssh.exe" : options.SshExecutablePath;

    private static void AddOption(ProcessStartInfo psi, string kv)
    {
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(kv);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
