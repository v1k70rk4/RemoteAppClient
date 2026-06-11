using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RemoteAgent.Configuration;

namespace RemoteAgent.Tunnel;

/// <summary>
/// Egyetlen reverse SSH tunnel életciklusa az <c>ssh.exe</c> köré.
/// A bástya host-kulcsa pinnelt (saját known_hosts), így az agent KIZÁRÓLAG
/// a mi szerverünkhöz tud csatlakozni — közbeékelődő szervernek nem.
/// A forward célja FIX (localhost:LocalForwardPort), a szerver csak a
/// távoli portszámot befolyásolhatja.
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
            logger.LogInformation("Tunnel már fut, új indítás kihagyva.");
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

        // -N: ne futtasson távoli parancsot, csak a forward.
        // -R remote:localhost:local — a reverse forward.
        psi.ArgumentList.Add("-N");
        psi.ArgumentList.Add("-R");
        // 127.0.0.1 (nem 'localhost') — a Windows ssh a localhostot ::1-re oldhatja,
        // de a VNC tipikusan csak IPv4 loopbacken figyel.
        psi.ArgumentList.Add($"{remotePort}:127.0.0.1:{options.LocalForwardPort}");

        // Csak a megadott kulccsal, jelszós/interaktív próba nélkül.
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(options.PrivateKeyPath);

        // A CA által aláírt SSH-cert (a bástya TrustedUserCAKeys-szel bízik benne).
        if (!string.IsNullOrWhiteSpace(options.CertificatePath))
            AddOption(psi, $"CertificateFile=\"{options.CertificatePath}\"");

        AddOption(psi, "IdentitiesOnly=yes");
        AddOption(psi, "BatchMode=yes");                       // semmilyen prompt
        AddOption(psi, "StrictHostKeyChecking=yes");           // ismeretlen kulcsnál bukik
        AddOption(psi, $"UserKnownHostsFile=\"{_knownHostsPath}\"");
        AddOption(psi, "ExitOnForwardFailure=yes");            // ha a forward nem jön létre, lépjen ki
        AddOption(psi, "ServerAliveInterval=15");              // keepalive
        AddOption(psi, "ServerAliveCountMax=3");

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(options.BastionPort.ToString());
        psi.ArgumentList.Add($"{options.BastionUser}@{options.BastionHost}");

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // Az ssh csak hibát/figyelmeztetést ír a stderr-re (nincs -v) — Warning szinten
        // logoljuk, hogy a SYSTEM service EventLogjában is látszódjon a tunnel-hiba oka.
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                logger.LogWarning("ssh: {Line}", e.Data);
        };

        logger.LogInformation(
            "Reverse tunnel indítása: bástya {Host}:{Port}, távoli port {Remote} -> helyi {Local}.",
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
            logger.LogWarning(ex, "Tunnel leállításakor hiba.");
        }
        finally
        {
            proc.Dispose();
            _process = null;
            CleanupKnownHosts();
            logger.LogInformation("Reverse tunnel lebontva.");
        }
    }

    private string WritePinnedKnownHosts()
    {
        // A pinnelt host-kulcsot egy ideiglenes, csak ehhez a sessionhöz tartozó
        // known_hosts fájlba írjuk. Tartalma: "<host> <kulcs>".
        var path = Path.Combine(Path.GetTempPath(), $"ra_known_hosts_{Guid.NewGuid():N}");
        // A 22-es (alapértelmezett) portnál az ssh a sima hostnevet keresi a known_hosts-ban;
        // a [host]:port forma csak nem-alapértelmezett portnál érvényes.
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

    private string ResolveSshPath() =>
        string.IsNullOrWhiteSpace(options.SshExecutablePath) ? "ssh.exe" : options.SshExecutablePath;

    private static void AddOption(ProcessStartInfo psi, string kv)
    {
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(kv);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
