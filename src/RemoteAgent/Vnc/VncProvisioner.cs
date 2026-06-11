using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace RemoteAgent.Vnc;

/// <summary>
/// Telepíti és bekonfigurálja a TightVNC szervert: csendes MSI-telepítés, majd
/// registry-hardening (loopback-only, HTTP ki, gépenként egyedi jelszó). Idempotens:
/// ha a service már fut, csak a hardeninget alkalmazza. SYSTEM/admin jog kell hozzá.
/// </summary>
public static class VncProvisioner
{
    private const string ServerKey = @"SOFTWARE\TightVNC\Server";
    private const string ServiceName = "tvnserver";

    /// <summary>CLI belépés: RemoteAgent.exe provision-vnc [--msi path] [--password p]</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var msi = GetArg(args, "--msi") ?? Path.Combine(AppContext.BaseDirectory, "vnc", "tightvnc.msi");
        var password = GetArg(args, "--password") ?? GeneratePassword();

        try
        {
            var installed = await EnsureInstalledAsync(msi);
            ApplyHardening(password);
            Console.WriteLine(installed ? "TightVNC telepítve és bekonfigurálva." : "TightVNC már telepítve; konfiguráció frissítve.");
            Console.WriteLine($"  VNC port:  127.0.0.1:5900 (loopback-only)");
            Console.WriteLine($"  jelszó:    {password}");
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Nincs jogosultság (admin/SYSTEM kell az MSI-telepítéshez és a HKLM íráshoz).");
            return 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("VNC provisioning hiba: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Telepíti a TightVNC-t, ha még nincs. Visszaadja: most telepítettük-e.</summary>
    public static async Task<bool> EnsureInstalledAsync(string msiPath)
    {
        if (ServiceExists())
            return false;

        if (!File.Exists(msiPath))
            throw new FileNotFoundException("A TightVNC MSI nem található.", msiPath);

        var psi = new ProcessStartInfo("msiexec", $"/i \"{msiPath}\" /quiet /norestart ADDLOCAL=Server")
        {
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        // 0 = siker, 3010 = siker, újraindítás ajánlott.
        if (proc.ExitCode is not (0 or 3010))
            throw new InvalidOperationException($"msiexec hibakód: {proc.ExitCode}");
        return true;
    }

    /// <summary>Registry-hardening + a service újraindítása, hogy életbe lépjen.</summary>
    public static void ApplyHardening(string password)
    {
        using (var key = Registry.LocalMachine.CreateSubKey(ServerKey, writable: true)
                         ?? throw new UnauthorizedAccessException(ServerKey))
        {
            key.SetValue("RfbPort", 5900, RegistryValueKind.DWord);
            key.SetValue("AllowLoopback", 1, RegistryValueKind.DWord);
            key.SetValue("LoopbackOnly", 1, RegistryValueKind.DWord);          // csak 127.0.0.1
            key.SetValue("AcceptHttpConnections", 0, RegistryValueKind.DWord); // 5800-as web/Java port ki
            key.SetValue("UseVncAuthentication", 1, RegistryValueKind.DWord);
            key.SetValue("Password", VncPassword.Encrypt(password), RegistryValueKind.Binary);
        }

        // A futó szerver csak újraindításkor olvassa újra a configot.
        RestartService(ServiceName);
    }

    // 'net' szinkron (megvárja a leállást/indulást) — az 'sc stop; sc start' versenyhelyzetet okoz.
    private static void RestartService(string service)
    {
        RunNet("stop", service);   // ha nem fut, hibakód → eldobjuk
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
        return proc.ExitCode == 0; // 1060 = nincs ilyen service
    }

    /// <summary>8 karakteres random jelszó (a klasszikus VncAuth úgyis 8 bájtot néz).</summary>
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
