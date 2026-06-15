using System.Text.Json;

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

    /// <summary>Theme mode: "light" | "dark" | "auto" (auto follows Windows settings).</summary>
    public string ThemeMode { get; set; } = "dark";

    /// <summary>Release channel for self-update: "rtm" (default) or "beta".</summary>
    public string Channel { get; set; } = "rtm";

    /// <summary>Windows Hello credential ID registered with the server on this device (null = not configured).</summary>
    public Guid? HelloCredentialId { get; set; }

    /// <summary>Username associated with Hello for passwordless sign-in.</summary>
    public string? HelloUsername { get; set; }

    /// <summary>"Remember this device" 2FA-trust token: lets the server skip TOTP for ~90 days. Useless without the password.</summary>
    public string? TrustToken { get; set; }

    /// <summary>The username the trust token belongs to (prefilled on the login screen and matched before sending the token).</summary>
    public string? TrustUsername { get; set; }

    /// <summary>VNC session panel layout (local, per machine): "split" (viewer 80% + panel 20%),
    /// "background" (viewer 100%, panel opens behind it), or "off" (viewer 100%, no panel).</summary>
    public string VncPanelMode { get; set; } = "split";

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(SshHost) &&
        !string.IsNullOrWhiteSpace(SshUser) &&
        !string.IsNullOrWhiteSpace(SshKeyPath);

    public static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RemoteClient", "config.json");

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
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
