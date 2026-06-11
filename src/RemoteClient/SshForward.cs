using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace RemoteClient;

/// <summary>
/// Egy helyi SSH local-forward (ssh -L) a boxra: 127.0.0.1:LocalPort → a box
/// 127.0.0.1:remotePort-ja. Ezzel éri el az admin a (localhost-only) admin API-t és
/// a bástya reverse-tunnel VNC-portjait. A folyamatot Dispose-kor leállítja.
/// </summary>
public sealed class SshForward : IDisposable
{
    private readonly Process _proc;
    public int LocalPort { get; }

    public SshForward(ClientConfig cfg, int remotePort)
    {
        LocalPort = FreeLocalPort();

        var psi = new ProcessStartInfo(cfg.SshExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-N");
        psi.ArgumentList.Add("-L");
        psi.ArgumentList.Add($"127.0.0.1:{LocalPort}:127.0.0.1:{remotePort}");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(cfg.SshKeyPath);
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(cfg.SshPort.ToString());
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("StrictHostKeyChecking=accept-new");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ExitOnForwardFailure=yes");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ServerAliveInterval=15");
        psi.ArgumentList.Add($"{cfg.SshUser}@{cfg.SshHost}");

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("ssh nem indult el.");
    }

    public bool IsRunning => !_proc.HasExited;

    private static int FreeLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
        _proc.Dispose();
    }
}
