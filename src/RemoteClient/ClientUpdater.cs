using System.Diagnostics;
using System.Security.Cryptography;

namespace RemoteClient;

/// <summary>
/// Console client self-update: after sign-in, checks the current 'client' version on its
/// channel and, when newer, downloads it with SHA-256 verification, replaces the running
/// executable, and restarts. Works cleanly only with the published single-file exe because
/// DLLs cannot be replaced while loaded.
/// </summary>
public static class ClientUpdater
{
    /// <summary>Startup cleanup: deletes .old executables left by previous updates, best effort.</summary>
    public static void CleanupOld()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// If a newer 'client' package exists on the channel, downloads, verifies, replaces the running exe,
    /// starts the new one, and returns true so the caller can exit. Otherwise returns false.
    /// </summary>
    public static async Task<bool> CheckAndUpdateAsync(AdminApi api, string channel)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var packages = await api.GetChannelsAsync();
            var pkg = packages.FirstOrDefault(p =>
                string.Equals(p.Component, "client", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.Channel, channel, StringComparison.OrdinalIgnoreCase));
            if (pkg is null || string.IsNullOrWhiteSpace(pkg.FileName)) return false;

            if (!Version.TryParse(pkg.Version, out var newVer) || newVer <= RunningVersion()) return false;

            var tmp = exe + ".new";
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            await api.DownloadUpdateAsync(pkg.FileName, tmp);

            if (!await HashMatchesAsync(tmp, pkg.Sha256))
            {
                try { File.Delete(tmp); } catch { }
                return false;
            }

            // Replacement: rename the running exe (Windows allows this), put the new one in place, then start it.
            var old = exe + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            File.Move(exe, old);
            File.Move(tmp, exe);

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false; // on any error, stay on the current version
        }
    }

    /// <summary>Running executable version as text for login requests and the minimum-version gate.</summary>
    public static string RunningVersionString() => RunningVersion().ToString();

    /// <summary>
    /// Applies a server-mandated update with known file name and hash, without channel lookup:
    /// downloads, verifies, replaces the running exe, and starts the new one. true means caller must exit.
    /// </summary>
    public static async Task<bool> ApplyKnownAsync(AdminApi api, string fileName, string? sha256)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || string.IsNullOrWhiteSpace(fileName)) return false;

            var tmp = exe + ".new";
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            await api.DownloadUpdateAsync(fileName, tmp);

            if (!await HashMatchesAsync(tmp, sha256))
            {
                try { File.Delete(tmp); } catch { }
                return false;
            }

            var old = exe + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            File.Move(exe, old);
            File.Move(tmp, exe);

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    private static Version RunningVersion()
    {
        try
        {
            var exe = Environment.ProcessPath!;
            var fi = FileVersionInfo.GetVersionInfo(exe);
            return new Version(Math.Max(0, fi.FileMajorPart), Math.Max(0, fi.FileMinorPart), Math.Max(0, fi.FileBuildPart), Math.Max(0, fi.FilePrivatePart));
        }
        catch { return new Version(0, 0, 0, 0); }
    }

    private static async Task<bool> HashMatchesAsync(string path, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return false;
        await using var fs = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs));
        return string.Equals(actual, expected.Replace(":", "").Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
