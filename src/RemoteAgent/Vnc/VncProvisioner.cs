using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Vnc;

/// <summary>
/// Installs and configures the TightVNC server: silent MSI install followed by registry
/// hardening (loopback-only, HTTP disabled, unique per-device password). Idempotent:
/// if the service already runs, only hardening is applied. Requires SYSTEM/admin rights.
/// </summary>
public static class VncProvisioner
{
    private const string ServerKey = @"SOFTWARE\TightVNC\Server";
    private const string ServiceName = "tvnserver";

    /// <summary>CLI entry: RemoteAgent.exe provision-vnc [--msi path] [--password p]</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var msi = GetArg(args, "--msi") ?? Path.Combine(AppContext.BaseDirectory, "vnc", "tightvnc.msi");
        var password = GetArg(args, "--password") ?? GeneratePassword();

        try
        {
            var installed = await EnsureInstalledAsync(msi);
            ApplyHardening(password);
            Console.WriteLine(installed ? L.VncProvisioner_TightVNCInstalledAndConfigured : L.VncProvisioner_TightVNCAlreadyInstalledConfigurationUpdated);
            Console.WriteLine($"  VNC port:  127.0.0.1:5900 (loopback-only)");
            Console.WriteLine(L.Format(L.VncProvisioner_Password, password));
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine(L.VncProvisioner_InsufficientPrivilegesAdminSYSTEMRequired);
            return 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(L.VncProvisioner_VNCProvisioningError + ex.Message);
            return 1;
        }
    }

    /// <summary>Installs TightVNC when missing. Returns whether it was installed now.</summary>
    public static async Task<bool> EnsureInstalledAsync(string msiPath)
    {
        if (ServiceExists())
            return false;

        if (!File.Exists(msiPath))
            throw new FileNotFoundException(L.VncProvisioner_TightVNCMSINotFound, msiPath);

        // ADDLOCAL=ALL installs both the Server (incoming) and the Viewer (tvnviewer.exe). Console machines
        // run the same package, and the client launches tvnviewer.exe, so the viewer must be present too.
        var psi = new ProcessStartInfo("msiexec", $"/i \"{msiPath}\" /quiet /norestart ADDLOCAL=ALL")
        {
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        // 0 = success, 3010 = success with reboot recommended.
        if (proc.ExitCode is not (0 or 3010))
            throw new InvalidOperationException(L.Format(L.VncProvisioner_MsiexecExitCode, proc.ExitCode));
        return true;
    }

    /// <summary>
    /// Watchdog: keeps tvnserver installed, hardened and running. Re-hardens only on configuration
    /// drift (which restarts the service), so a healthy server is left untouched on every tick.
    /// </summary>
    private static bool _viewerRepairTried;

    public static async Task EnsureHealthyAsync(string password, string msiPath)
    {
        if (!ServiceExists())
        {
            await EnsureInstalledAsync(msiPath);
            ApplyHardening(password); // writes config + starts the service (ADDLOCAL=ALL also brings the viewer)
        }
        else if (!IsHardened(password))
        {
            ApplyHardening(password); // re-applies config and restarts (also brings it up if stopped)
        }
        else if (!IsRunning())
        {
            RunNet("start", ServiceName);
        }

        // Viewer self-heal: console machines need tvnviewer.exe even when only the Server was installed
        // (older "vnc" rollouts used ADDLOCAL=Server). Add it via maintenance on the cached package — no
        // download needed — then re-harden, since reconfigure can reset the server config. Once per run.
        if (!_viewerRepairTried && ServiceExists() && !IsViewerInstalled())
        {
            _viewerRepairTried = true;
            if (EnsureViewer())
                ApplyHardening(password);
        }
    }

    /// <summary>Whether the TightVNC viewer (tvnviewer.exe) is present in any known TightVNC install dir.</summary>
    public static bool IsViewerInstalled()
    {
        foreach (var dir in ViewerDirs())
            if (File.Exists(Path.Combine(dir, "tvnviewer.exe"))) return true;
        return false;
    }

    private static IEnumerable<string> ViewerDirs()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(pf, "TightVNC");
        if (!string.IsNullOrEmpty(pfx86)) yield return Path.Combine(pfx86, "TightVNC");
        foreach (var (_, _, loc) in EnumTightVnc())
            if (!string.IsNullOrWhiteSpace(loc)) yield return loc!;
    }

    /// <summary>Reconfigures the installed TightVNC to add all features (incl. Viewer) via the cached MSI.</summary>
    private static bool EnsureViewer()
    {
        var pc = EnumTightVnc().Select(e => e.subKey).FirstOrDefault();
        if (string.IsNullOrEmpty(pc)) return false;
        try
        {
            var psi = new ProcessStartInfo("msiexec", $"/i {pc} ADDLOCAL=ALL /quiet /norestart")
            { UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode is 0 or 3010;
        }
        catch { return false; }
    }

    /// <summary>Whether tvnserver is currently in the RUNNING state.</summary>
    public static bool IsRunning()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("sc", $"query {ServiceName}")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
            var outp = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return outp.Contains("RUNNING", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>Whether the hardened config (loopback-only, no HTTP, our password) is present in both registry views.</summary>
    public static bool IsHardened(string password)
    {
        var enc = VncPassword.Encrypt(password);
        return IsHardenedView(RegistryView.Registry64, enc) && IsHardenedView(RegistryView.Registry32, enc);
    }

    private static bool IsHardenedView(RegistryView view, byte[] enc)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey(ServerKey);
            if (key is null) return false;
            bool Dword(string n, int v) => key.GetValue(n) is int x && x == v;
            if (!(Dword("RfbPort", 5900) && Dword("LoopbackOnly", 1)
                  && Dword("AcceptHttpConnections", 0) && Dword("UseVncAuthentication", 1)
                  && Dword("AlwaysShared", 1) && Dword("DisconnectClients", 0)))
                return false;
            return key.GetValue("Password") is byte[] p && p.AsSpan().SequenceEqual(enc);
        }
        catch { return false; }
    }

    /// <summary>Applies registry hardening and restarts the service so it takes effect.</summary>
    public static void ApplyHardening(string password)
    {
        var encrypted = VncPassword.Encrypt(password);
        // Write both registry views: 64-bit tvnserver reads the normal key, 32-bit reads Wow6432Node.
        // (pl. Program Files (x86) alatti) a WOW6432Node-ot olvassa.
        WriteServerConfig(RegistryView.Registry64, encrypted);
        WriteServerConfig(RegistryView.Registry32, encrypted);

        // The running server reloads config only after restart.
        RestartService(ServiceName);
    }

    private static void WriteServerConfig(RegistryView view, byte[] encryptedPassword)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = baseKey.CreateSubKey(ServerKey, writable: true)
                        ?? throw new UnauthorizedAccessException(ServerKey);
        key.SetValue("RfbPort", 5900, RegistryValueKind.DWord);
        key.SetValue("AllowLoopback", 1, RegistryValueKind.DWord);
        key.SetValue("LoopbackOnly", 1, RegistryValueKind.DWord);          // csak 127.0.0.1
        key.SetValue("AcceptHttpConnections", 0, RegistryValueKind.DWord); // 5800-as web/Java port ki
        key.SetValue("UseVncAuthentication", 1, RegistryValueKind.DWord);
        key.SetValue("AlwaysShared", 1, RegistryValueKind.DWord);          // több néző egyszerre (megosztott)
        key.SetValue("DisconnectClients", 0, RegistryValueKind.DWord);     // új kapcsolat ne bontsa a meglévőt
        key.SetValue("Password", encryptedPassword, RegistryValueKind.Binary);
    }

    // 'net' is synchronous and waits for stop/start; 'sc stop; sc start' can race.
    private static void RestartService(string service)
    {
        RunNet("stop", service);   // non-running service returns an error; ignore it
        RunNet("start", service);
    }

    private static void RunNet(string verb, string service)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("net", $"{verb} \"{service}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            proc.WaitForExit();
        }
        catch { /* best effort */ }
    }

    private static bool ServiceExists()
    {
        using var proc = Process.Start(new ProcessStartInfo("sc", $"query {ServiceName}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        proc.WaitForExit();
        return proc.ExitCode == 0; // 1060 = no such service
    }

    private const string ArpKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// Uninstall cleanup: stop + delete the service and force-remove TightVNC's files and registry.
    /// We cannot run "msiexec /x" here because it would run inside our own MSI transaction (mutex 1618),
    /// so this removes the footprint directly. Best-effort; invoked by the MSI uninstall (remove-vnc).
    /// </summary>
    public static int Remove()
    {
        RunSc("stop", ServiceName);
        RunSc("delete", ServiceName);

        var entries = EnumTightVnc().ToList();

        // Program files: ARP InstallLocation plus the default locations.
        var dirs = entries.Select(e => e.installLocation).Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d!).ToList();
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        dirs.Add(Path.Combine(pf, "TightVNC"));
        if (!string.IsNullOrEmpty(pfx86)) dirs.Add(Path.Combine(pfx86, "TightVNC"));
        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* in use / best effort */ }

        // Registry: server config (both views) + the ARP entries.
        DeleteKeyTree(RegistryView.Registry64, ServerKey);
        DeleteKeyTree(RegistryView.Registry32, ServerKey);
        foreach (var (view, subKey, _) in entries)
            DeleteKeyTree(view, ArpKey + "\\" + subKey);

        Console.WriteLine("TightVNC removed.");
        return 0;
    }

    /// <summary>ARP entries whose DisplayName starts with "TightVNC", from both registry views.</summary>
    private static IEnumerable<(RegistryView view, string subKey, string? installLocation)> EnumTightVnc()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            RegistryKey? uninstall = null;
            try { uninstall = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(ArpKey); }
            catch { /* best effort */ }
            if (uninstall is null) continue;
            using (uninstall)
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    string? display = null, loc = null;
                    try
                    {
                        using var sk = uninstall.OpenSubKey(name);
                        display = sk?.GetValue("DisplayName") as string;
                        loc = sk?.GetValue("InstallLocation") as string;
                    }
                    catch { /* best effort */ }
                    if (display is not null && display.StartsWith("TightVNC", StringComparison.OrdinalIgnoreCase))
                        yield return (view, name, loc);
                }
        }
    }

    private static void DeleteKeyTree(RegistryView view, string path)
    {
        try
        {
            using var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            b.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch { /* best effort */ }
    }

    private static void RunSc(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            proc.WaitForExit();
        }
        catch { /* best effort */ }
    }

    /// <summary>8-character random password; classic VncAuth uses only 8 bytes anyway.</summary>
    public static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        Span<char> chars = stackalloc char[8];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
