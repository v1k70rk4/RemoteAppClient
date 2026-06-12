using System.Diagnostics;
using System.Security.Cryptography;

namespace RemoteClient;

/// <summary>
/// A konzol-kliens önfrissítése: belépés után megnézi a saját csatornáján a 'client' aktuális
/// verzióját, és ha újabb, letölti (SHA-256 ellenőrzéssel), kicseréli a futó exét és újraindul.
/// Csak a PUBLISH-elt, egyfájlos exével működik tisztán (egy DLL-t nem lehet futás közben cserélni).
/// </summary>
public static class ClientUpdater
{
    /// <summary>Indításkor: a korábbi frissítés után maradt .old exe törlése (best effort).</summary>
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
    /// Ha van újabb 'client' csomag a megadott csatornán: letölti, ellenőrzi, kicseréli a futó exét,
    /// elindítja az újat és true-val tér vissza (a hívónak ki kell lépnie). Egyébként false.
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

            // Csere: a futó exét átnevezzük (Windows engedi), az újat a helyére tesszük, majd indítjuk.
            var old = exe + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            File.Move(exe, old);
            File.Move(tmp, exe);

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false; // bármi hiba → maradunk a jelenlegi verzión
        }
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
