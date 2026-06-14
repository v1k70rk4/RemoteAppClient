using System.Text.Json;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient;

/// <summary>A helyi agent enrollment-adatainak olvasása (a kliens csak olvassa az enrollment.json-t).</summary>
public static class AgentInfo
{
    private const string EnrollmentPath = @"C:\ProgramData\RemoteAgent\enrollment.json";

    public static string? ServerUrl()
    {
        try
        {
            if (!File.Exists(EnrollmentPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(EnrollmentPath));
            return doc.RootElement.TryGetProperty("serverUrl", out var u) ? u.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>A szerver megjelenítendő neve (host) az enrollmentből.</summary>
    public static string ServerName()
    {
        var url = ServerUrl();
        if (string.IsNullOrWhiteSpace(url)) return L.AgentInfo_001;
        try { return new Uri(url).Host; } catch { return url; }
    }
}
