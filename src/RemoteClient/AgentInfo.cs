using System.Text.Json;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient;

/// <summary>Reads local agent enrollment data. The client only reads enrollment.json.</summary>
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

    /// <summary>Display server name (host) from enrollment.</summary>
    public static string ServerName()
    {
        var url = ServerUrl();
        if (string.IsNullOrWhiteSpace(url)) return L.AgentInfo_UnknownServer;
        try { return new Uri(url).Host; } catch { return url; }
    }
}
