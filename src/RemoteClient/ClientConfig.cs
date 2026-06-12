using System.Text.Json;

namespace RemoteClient;

/// <summary>
/// Az admin-client beállításai (%APPDATA%\RemoteClient\config.json). A bizalmi
/// gyökér az admin SSH-hozzáférése a boxhoz — SSH-n éri el az admin API-t és a
/// bástya VNC-portjait. Nincs külön szerver-oldali admin-auth (v1).
/// </summary>
public sealed class ClientConfig
{
    public string SshHost { get; set; } = "";
    public string SshUser { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string SshKeyPath { get; set; } = "";
    public string SshExe { get; set; } = @"C:\Windows\System32\OpenSSH\ssh.exe";
    public string ViewerExe { get; set; } = @"C:\Program Files\TightVNC\tvnviewer.exe";

    /// <summary>A szerver admin API portja a boxon (Kestrel, localhost).</summary>
    public int AdminApiPort { get; set; } = 5000;

    /// <summary>Sötét téma (MaterialSkin). False = világos.</summary>
    public bool DarkTheme { get; set; } = true;

    /// <summary>Release-csatorna az önfrissítéshez: "rtm" (alap) vagy "beta".</summary>
    public string Channel { get; set; } = "rtm";

    /// <summary>Windows Hello: a szerverhez regisztrált hitelesítő azonosítója ezen a gépen (null = nincs beállítva).</summary>
    public Guid? HelloCredentialId { get; set; }

    /// <summary>A Hello-hoz tartozó felhasználónév (a passwordless belépéshez).</summary>
    public string? HelloUsername { get; set; }

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
        catch { /* hibás config → alapértelmezett */ }
        return new ClientConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
