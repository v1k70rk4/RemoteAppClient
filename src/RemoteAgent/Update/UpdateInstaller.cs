using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Configuration;
using RemoteAgent.Security;

namespace RemoteAgent.Update;

/// <summary>
/// Az aláírt update-parancs végrehajtása: letölti a csomagot (mTLS-sel a szerverről),
/// ELLENŐRZI a SHA-256-ot, stagingbe teszi (&lt;EnrollmentDir&gt;\update\…) és kiír egy
/// markert a célpathszal.
///
/// A cserét a MÁSIK processz végzi (futó exe nem cserélheti a sajátját):
///  - "agent"   csomag → a Helper (RemoteAgent.Updater) cseréli   (update.ready)
///  - "updater" csomag → az Agent (HelperUpdateWatcher) cseréli   (update.updater.ready)
/// A parancs aláírása + a hash adja a biztonságot.
/// </summary>
public sealed class UpdateInstaller(IOptions<AgentOptions> options, ILogger<UpdateInstaller> logger)
{
    private readonly AgentOptions _opt = options.Value;
    private readonly string _dir = Path.Combine(options.Value.EnrollmentDir, "update");

    public async Task ApplyAsync(string? target, string? version, string? url, string? sha256, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(sha256))
        {
            logger.LogWarning("Update parancs URL/hash nélkül, kihagyva.");
            return;
        }

        // A "vnc" csomag MSI — nem exe-csere, hanem msiexec helyben (a futó exét nem érinti).
        if (string.Equals(target, "vnc", StringComparison.OrdinalIgnoreCase))
        {
            await ApplyVncMsiAsync(version, url, sha256, ct);
            return;
        }

        // A "client" a mellé telepített konzol-kliens (RemoteClient.exe). NEM az agent exéje és nem
        // service, ezért az agent maga felülírhatja (best-effort retry, ha épp fut valakinél).
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
            logger.LogWarning("Az Updater exe útvonala nem határozható meg — frissítés kihagyva.");
            return;
        }

        Directory.CreateDirectory(_dir);
        var tmp = Path.Combine(_dir, "staged.tmp");

        try
        {
            var resolved = ResolveUrl(url);
            logger.LogInformation("Update letöltése: {Target} {Version} ({Url})", isUpdater ? "updater" : "agent", version, resolved);

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
                logger.LogError("Update hash NEM egyezik (várt {Expected}, kapott {Actual}) — eldobva.", expected, actual);
                TryDelete(tmp);
                return;
            }

            var newExe = Path.Combine(_dir, stagedName);
            TryDelete(newExe);
            File.Move(tmp, newExe);

            await File.WriteAllTextAsync(Path.Combine(_dir, markerName), targetPath, ct);
            logger.LogInformation("Update staging kész ({Version}) — a csere átveszi a másik processz.", version);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update sikertelen.");
            TryDelete(tmp);
        }
    }

    /// <summary>
    /// TightVNC (vnc) frissítés: letölti az MSI-t, ELLENŐRZI a SHA-256-ot, majd helyben telepíti
    /// (msiexec /i … ADDLOCAL=Server — ahogy a provisioner is). In-place upgrade, a beállítások maradnak.
    /// </summary>
    private async Task ApplyVncMsiAsync(string? version, string url, string sha256, CancellationToken ct)
    {
        Directory.CreateDirectory(_dir);
        var msi = Path.Combine(_dir, "vnc-update.msi");
        try
        {
            var resolved = ResolveUrl(url);
            logger.LogInformation("TightVNC frissítés letöltése: {Version} ({Url})", version, resolved);

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
                logger.LogError("TightVNC update hash NEM egyezik (várt {Expected}, kapott {Actual}) — eldobva.", expected, actual);
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
            if (proc.ExitCode is 0 or 3010) // 3010 = siker, újraindítás javasolt
                logger.LogInformation("TightVNC frissítve ({Version}).", version);
            else
                logger.LogWarning("TightVNC msiexec hibakód: {Code}", proc.ExitCode);
        }
        catch (Exception ex) { logger.LogWarning(ex, "TightVNC frissítés sikertelen."); }
        finally { TryDelete(msi); }
    }

    /// <summary>
    /// Konzol-kliens (RemoteClient.exe) frissítése: letölti, ELLENŐRZI a SHA-256-ot, és felülírja
    /// az agent mellé telepített RemoteClient.exe-t. Ha épp fut (zárolt), néhányszor újrapróbálja.
    /// Így a kliens akkor is naprakész a gépen, ha senki nem indítja el (a self-update nem fut le).
    /// </summary>
    private async Task ApplyClientExeAsync(string? version, string url, string sha256, CancellationToken ct)
    {
        var targetPath = ClientExePath();
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            logger.LogWarning("A RemoteClient.exe útvonala nem határozható meg — kliens-frissítés kihagyva.");
            return;
        }

        Directory.CreateDirectory(_dir);
        var tmp = Path.Combine(_dir, "client.tmp");
        try
        {
            var resolved = ResolveUrl(url);
            logger.LogInformation("Kliens-frissítés letöltése: {Version} ({Url})", version, resolved);

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
                logger.LogError("Kliens update hash NEM egyezik (várt {Expected}, kapott {Actual}) — eldobva.", expected, actual);
                TryDelete(tmp);
                return;
            }

            // 1) Türelmi idő: ha épp fut/záródik, pár mp-ig várunk a cserével.
            bool copied = false;
            for (int i = 0; i < 5 && !copied; i++)
            {
                if (TryCopy(tmp, targetPath)) { copied = true; break; }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            // 2) Ha még mindig zárolt (nyitva tartott kliens), kilőjük a futó RemoteClient-et és újrapróbáljuk.
            if (!copied)
            {
                KillRunningClient();
                for (int i = 0; i < 5 && !copied; i++)
                {
                    if (TryCopy(tmp, targetPath)) { copied = true; break; }
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
            }

            if (copied) logger.LogInformation("Konzol-kliens frissítve ({Version}).", version);
            else logger.LogWarning("A RemoteClient.exe cseréje nem sikerült (zárolt?) — később újrapróbálható.");
        }
        catch (Exception ex) { logger.LogWarning(ex, "Kliens-frissítés sikertelen."); }
        finally { TryDelete(tmp); }
    }

    /// <summary>Megpróbálja felülírni a cél exét; false, ha zárolt (fut) vagy hozzáférés-hiba.</summary>
    private static bool TryCopy(string src, string dest)
    {
        try { File.Copy(src, dest, overwrite: true); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Kilövi a futó RemoteClient.exe-ket, hogy a frissítés át tudja írni az exét. (A user-t kidobja.)</summary>
    private void KillRunningClient()
    {
        foreach (var p in Process.GetProcessesByName("RemoteClient"))
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
                logger.LogWarning("Futó RemoteClient kilőve a frissítéshez (PID {Pid}).", p.Id);
            }
            catch (Exception ex) { logger.LogDebug(ex, "RemoteClient kilövése nem sikerült (PID {Pid}).", p.Id); }
            finally { p.Dispose(); }
        }
    }

    /// <summary>A konzol-kliens exe útvonala: az agent exe mellett (a telepítés így rakja le).</summary>
    private static string ClientExePath()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath);
        return string.IsNullOrEmpty(dir) ? "" : Path.Combine(dir, "RemoteClient.exe");
    }

    /// <summary>A Helper/Updater exe útvonala: az agent exe mellett van (a telepítés így rakja le).</summary>
    private static string UpdaterExePath()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath);
        return string.IsNullOrEmpty(dir) ? "" : Path.Combine(dir, "RemoteAgent.Updater.exe");
    }

    /// <summary>Relatív URL feloldása a szerver-bázishoz (a Telemetry.IngestUrl alapján).</summary>
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
