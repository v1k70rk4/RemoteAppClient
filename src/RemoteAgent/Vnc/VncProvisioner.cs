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

        var psi = new ProcessStartInfo("msiexec", $"/i \"{msiPath}\" /quiet /norestart ADDLOCAL=Server")
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
