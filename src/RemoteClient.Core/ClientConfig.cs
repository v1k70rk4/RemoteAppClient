using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteClient;

/// <summary>
/// Admin client settings (%APPDATA%\RemoteClient\config.json). Trust root is the admin's
/// SSH access to the box: SSH reaches the admin API and bastion VNC ports. There is no
/// separate server-side admin auth in v1.
/// </summary>
public sealed class ClientConfig
{
    public string SshHost { get; set; } = "";
    public string SshUser { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string SshKeyPath { get; set; } = "";
    public string SshExe { get; set; } = @"C:\Windows\System32\OpenSSH\ssh.exe";
    public string ViewerExe { get; set; } = @"C:\Program Files\TightVNC\tvnviewer.exe";

    /// <summary>Server admin API port on the box (Kestrel, localhost).</summary>
    public int AdminApiPort { get; set; } = 5000;

    /// <summary>Linux operator console: last server URL and username, prefilled on the next sign-in.</summary>
    public string? LastServerUrl { get; set; }
    public string? LastUsername { get; set; }

    /// <summary>Linux console VNC scale passed to ssvncviewer: "auto" (fit-to-window, follows resize/maximize -
    /// recommended), a fraction ("3/4"), a ratio ("0.8"), "WxH", or "none". (Note: "fit" renders tiny - avoid.)
    /// The operator can also adjust live with the s / + / - / 1-6 keys in the viewer.</summary>
    public string VncScale { get; set; } = "auto";

    /// <summary>Linux console VNC color depth: true = ssvncviewer -bgr233 (256-color, low bandwidth - good over a
    /// tunnel); false = full color. Adjustable from the Settings panel.</summary>
    public bool VncColor256 { get; set; } = true;

    /// <summary>Linux console UI language: "auto" (follow the OS culture), "en" or "hu". Applied once at
    /// startup; the Settings panel offers a switch that takes effect after a restart.</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Theme mode: "light" | "dark" | "auto" (auto follows Windows settings).</summary>
    public string ThemeMode { get; set; } = "dark";

    /// <summary>Release channel for self-update: "rtm" (default) or "beta".</summary>
    public string Channel { get; set; } = "rtm";

    /// <summary>Windows Hello credential ID registered with the server on this device (null = not configured).</summary>
    public Guid? HelloCredentialId { get; set; }

    /// <summary>Username associated with Hello for passwordless sign-in.</summary>
    public string? HelloUsername { get; set; }

    /// <summary>"Remember this device" 2FA-trust token: lets the server skip TOTP for ~90 days. Useless without the
    /// password, but it is still half of a sign-in, so it is not kept in the clear: on Windows it is DPAPI-sealed to
    /// the signed-in Windows user (<c>dpapi:</c> prefix), elsewhere the config file itself is owner-only.</summary>
    [JsonIgnore]
    public string? TrustToken
    {
        get => Unseal(TrustTokenStored);
        set => TrustTokenStored = Seal(value);
    }

    /// <summary>The stored form of <see cref="TrustToken"/>. Kept under the old JSON name, so a config written by
    /// an earlier console (the token in the clear) still loads; the next save seals it.</summary>
    [JsonPropertyName("TrustToken")]
    public string? TrustTokenStored { get; set; }

    private const string DpapiPrefix = "dpapi:";

    private static string? Seal(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || !OperatingSystem.IsWindows()) return raw;
        try { return DpapiPrefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(raw), null, DataProtectionScope.CurrentUser)); }
        catch { return raw; } // DPAPI unavailable (rare): better a working sign-in than a silently lost trust
    }

    private static string? Unseal(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return stored;
        if (!OperatingSystem.IsWindows()) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[DpapiPrefix.Length..]), null, DataProtectionScope.CurrentUser)); }
        catch { return null; } // another user's or machine's seal: the server simply asks for the code again
    }

    /// <summary>The username the trust token belongs to (prefilled on the login screen and matched before sending the token).</summary>
    public string? TrustUsername { get; set; }

    /// <summary>VNC session panel layout (local, per machine): "split" (viewer 80% + panel 20%),
    /// "background" (viewer 100%, panel opens behind it), or "off" (viewer 100%, no panel).</summary>
    public string VncPanelMode { get; set; } = "split";

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(SshHost) &&
        !string.IsNullOrWhiteSpace(SshUser) &&
        !string.IsNullOrWhiteSpace(SshKeyPath);

    /// <summary>Config subfolder under %APPDATA% (or ~/.config). The Lite console overrides this at startup so
    /// it keeps its own settings separate from the full Windows client.</summary>
    public static string AppFolderName { get; set; } = "RemoteClient";

    public static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppFolderName, "config.json");

    public static ClientConfig Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(Path)) ?? new ClientConfig();
        }
        catch { /* invalid config; use defaults */ }
        return new ClientConfig();
    }

    public void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        if (OperatingSystem.IsWindows()) { File.WriteAllText(Path, json); return; }
        // Owner-only on Linux: the file holds the trust token and the last server; ~/.config is often 755.
        try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { /* best effort */ }
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite };
        using (var f = new StreamWriter(new FileStream(Path, options))) f.Write(json);
        try { File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ } // the file may have pre-existed wider
    }
}
