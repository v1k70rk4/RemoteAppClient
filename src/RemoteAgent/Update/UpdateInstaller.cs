using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Configuration;
using RemoteAgent.Security;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Update;

/// <summary>
/// Executes signed update commands: downloads the package from the server over mTLS,
/// verifies SHA-256, stages it under &lt;EnrollmentDir&gt;\update, and writes a marker
/// containing the target path.
///
/// Replacement is done by the other process because a running executable cannot replace itself:
///  - "agent" package: Helper (RemoteAgent.Updater) replaces it via update.ready
///  - "updater" package: Agent (HelperUpdateWatcher) replaces it via update.updater.ready
/// Security comes from the command signature plus the package hash.
/// </summary>
public sealed class UpdateInstaller(IOptions<AgentOptions> options, ILogger<UpdateInstaller> logger)
{
    private readonly AgentOptions _opt = options.Value;
    private readonly string _dir = Path.Combine(options.Value.EnrollmentDir, "update");

    public async Task ApplyAsync(string? target, string? version, string? url, string? sha256, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(sha256))
        {
            logger.LogWarning(L.UpdateInstaller_001);
            return;
        }

        // The "vnc" package is an MSI: install it locally with msiexec, without replacing this process.
        if (string.Equals(target, "vnc", StringComparison.OrdinalIgnoreCase))
        {
            await ApplyVncMsiAsync(version, url, sha256, ct);
            return;
        }

        // The "client" package is the side-by-side console client (RemoteClient.exe), not the agent
        // executable and not a service, so the agent can overwrite it with best-effort retries.
        if (string.Equals(target, "client", StringComparison.OrdinalIgnoreCase))
        {
            await ApplyClientExeAsync(version, url, sha256, ct);
            return;
        }

        bool isUpdater = string.Equals(target, "updater", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(target, "helper", StringComparison.OrdinalIgnoreCase);
        string stagedName = isUpdater ? "RemoteAgent.Updater.exe" : "RemoteAgent.exe";
        string markerName = isUpdater ? "update.updater.ready" : "update.ready";
        string targetPath = isUpdater ? UpdaterExePath() : (Environment.ProcessPath ?? "");

        if (isUpdater && string.IsNullOrWhiteSpace(targetPath))
        {
            logger.LogWarning(L.UpdateInstaller_002);
            return;
        }

        Directory.CreateDirectory(_dir);
        var tmp = Path.Combine(_dir, "staged.tmp");

        try
        {
            var resolved = ResolveUrl(url);
            logger.LogInformation(L.UpdateInstaller_003, isUpdater ? "updater" : "agent", version, resolved);

            using (var http = BuildClient())
            using (var resp = await http.GetAsync(resolved, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs, ct);
            }

            var expected = sha256.Replace(":", "").Trim();
            var actual = Convert.ToHexString(await ComputeSha256Async(tmp, ct));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError(L.UpdateInstaller_004, expected, actual);
                TryDelete(tmp);
                return;
            }

            var newExe = Path.Combine(_dir, stagedName);
            TryDelete(newExe);
            File.Move(tmp, newExe);

            await File.WriteAllTextAsync(Path.Combine(_dir, markerName), targetPath, ct);
            logger.LogInformation(L.UpdateInstaller_005, version);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, L.UpdateInstaller_019);
            TryDelete(tmp);
        }
    }

    /// <summary>
    /// TightVNC (vnc) update: downloads the MSI, verifies SHA-256, then installs it locally
    /// with msiexec /i ... ADDLOCAL=Server, just like the provisioner. Settings are preserved.
    /// </summary>
    private async Task ApplyVncMsiAsync(string? version, string url, string sha256, CancellationToken ct)
    {
        Directory.CreateDirectory(_dir);
        var msi = Path.Combine(_dir, "vnc-update.msi");
        try
        {
            var resolved = ResolveUrl(url);
            logger.LogInformation(L.UpdateInstaller_006, version, resolved);

            using (var http = BuildClient())
            using (var resp = await http.GetAsync(resolved, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(msi);
                await resp.Content.CopyToAsync(fs, ct);
            }

            var expected = sha256.Replace(":", "").Trim();
            var actual = Convert.ToHexString(await ComputeSha256Async(msi, ct));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError(L.UpdateInstaller_007, expected, actual);
                TryDelete(msi);
                return;
            }

            var psi = new ProcessStartInfo("msiexec", $"/i \"{msi}\" /quiet /norestart ADDLOCAL=Server")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode is 0 or 3010) // 3010 = success, reboot recommended
                logger.LogInformation(L.UpdateInstaller_008, version);
            else
                logger.LogWarning(L.UpdateInstaller_009, proc.ExitCode);
        }
        catch (Exception ex) { logger.LogWarning(ex, L.UpdateInstaller_010); }
        finally { TryDelete(msi); }
    }

    /// <summary>
    /// Console client (RemoteClient.exe) update: downloads it, verifies SHA-256, and overwrites
    /// the copy installed next to the agent. If it is running and locked, retry a few times.
    /// This keeps the client current even when nobody launches it and self-update never runs.
    /// </summary>
    private async Task ApplyClientExeAsync(string? version, string url, string sha256, CancellationToken ct)
    {
        var targetPath = ClientExePath();
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            logger.LogWarning(L.UpdateInstaller_011);
            return;
        }

        Directory.CreateDirectory(_dir);
        var tmp = Path.Combine(_dir, "client.tmp");
        try
        {
            var resolved = ResolveUrl(url);
            logger.LogInformation(L.UpdateInstaller_012, version, resolved);

            using (var http = BuildClient())
            using (var resp = await http.GetAsync(resolved, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs, ct);
            }

            var expected = sha256.Replace(":", "").Trim();
            var actual = Convert.ToHexString(await ComputeSha256Async(tmp, ct));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError(L.UpdateInstaller_013, expected, actual);
                TryDelete(tmp);
                return;
            }

            // 1) Grace period: if it is running or closing, wait a few seconds before replacement.
            bool copied = false;
            for (int i = 0; i < 5 && !copied; i++)
            {
                if (TryCopy(tmp, targetPath)) { copied = true; break; }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            // 2) If still locked by an open client, kill RemoteClient and retry.
            if (!copied)
            {
                KillRunningClient();
                for (int i = 0; i < 5 && !copied; i++)
                {
                    if (TryCopy(tmp, targetPath)) { copied = true; break; }
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
            }

            if (copied) logger.LogInformation(L.UpdateInstaller_014, version);
            else logger.LogWarning(L.UpdateInstaller_015);
        }
        catch (Exception ex) { logger.LogWarning(ex, L.UpdateInstaller_016); }
        finally { TryDelete(tmp); }
    }

    /// <summary>Tries to overwrite the target executable; false if locked or access is denied.</summary>
    private static bool TryCopy(string src, string dest)
    {
        try { File.Copy(src, dest, overwrite: true); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Kills running RemoteClient.exe instances so the update can overwrite the executable. This signs the user out.</summary>
    private void KillRunningClient()
    {
        foreach (var p in Process.GetProcessesByName("RemoteClient"))
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
                logger.LogWarning(L.UpdateInstaller_017, p.Id);
            }
            catch (Exception ex) { logger.LogDebug(ex, L.UpdateInstaller_018, p.Id); }
            finally { p.Dispose(); }
        }
    }

    /// <summary>Console client executable path, installed next to the agent executable.</summary>
    private static string ClientExePath()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath);
        return string.IsNullOrEmpty(dir) ? "" : Path.Combine(dir, "RemoteClient.exe");
    }

    /// <summary>Helper/Updater executable path, installed next to the agent executable.</summary>
    private static string UpdaterExePath()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath);
        return string.IsNullOrEmpty(dir) ? "" : Path.Combine(dir, "RemoteAgent.Updater.exe");
    }

    /// <summary>Resolves a relative URL against the server base inferred from Telemetry.IngestUrl.</summary>
    private string ResolveUrl(string url)
    {
        if (url.StartsWith('/') && !string.IsNullOrWhiteSpace(_opt.Telemetry.IngestUrl))
        {
            var b = new Uri(_opt.Telemetry.IngestUrl);
            return $"{b.Scheme}://{b.Authority}{url}";
        }
        return url;
    }

    private HttpClient BuildClient()
    {
        var handler = new SocketsHttpHandler();
        if (!string.IsNullOrWhiteSpace(_opt.ClientCertPfxPath) || !string.IsNullOrWhiteSpace(_opt.Telemetry.ClientCertThumbprint))
        {
            handler.SslOptions.ClientCertificates ??= new();
            handler.SslOptions.ClientCertificates.Add(
                CertHelper.ResolveClientCertificate(_opt.ClientCertPfxPath, _opt.Telemetry.ClientCertThumbprint));
        }
        if (!string.IsNullOrWhiteSpace(_opt.Telemetry.ServerCertPinSha256))
            handler.SslOptions.RemoteCertificateValidationCallback =
                CertHelper.PinnedServerValidator(_opt.Telemetry.ServerCertPinSha256);
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    }

    private static async Task<byte[]> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return await SHA256.HashDataAsync(fs, ct);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
