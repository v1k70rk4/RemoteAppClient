using System.Text.Json;
using RemoteAgent.Admin;
using RemoteAgent.Commands;

namespace RemoteClient;

/// <summary>
/// Local cache for server branding (owner + support), so it is visible before sign-in
/// and offline. Stored at %ProgramData%\RemoteAppClient\branding.json.
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
        catch { /* cache is non-critical */ }
    }
}
