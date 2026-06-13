using System.Text.Json;
using RemoteAgent.Admin;
using RemoteAgent.Commands;

namespace RemoteClient;

/// <summary>
/// A szerver branding-jének (tulajdonos + support) lokális gyorsítótára, hogy bejelentkezés
/// előtt és offline is megjeleníthető legyen. %ProgramData%\RemoteAppClient\branding.json.
/// </summary>
public static class BrandingCache
{
    private static string PathFile =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RemoteAppClient", "branding.json");

    public static BrandingInfo? Load()
    {
        try
        {
            if (!File.Exists(PathFile)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(PathFile), AgentJsonContext.Default.BrandingInfo);
        }
        catch { return null; }
    }

    public static void Save(BrandingInfo b)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(PathFile)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(b, AgentJsonContext.Default.BrandingInfo));
        }
        catch { /* a cache nem kritikus */ }
    }
}
