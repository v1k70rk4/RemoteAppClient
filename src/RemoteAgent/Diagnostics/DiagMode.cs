using System.Globalization;
using System.Text.Json;

namespace RemoteAgent.Diagnostics;

/// <summary>
/// Verbose logging on demand. The console's "diag" command switches it on for a few hours; while it is on,
/// the agent logs at Debug into its file log and TightVNC logs at a detailed level, and it switches itself
/// off when the time is up, restarts included.
///
/// The whole state is one file, <c>diag.json</c> next to the enrollment: it carries the expiry and a
/// <c>Logging</c> section the host reads as a reloadable configuration source, so writing or deleting the
/// file changes the live log level without restarting the service. Nothing is cached here on purpose - the
/// file is the truth, and it is tiny.
/// </summary>
public static class DiagMode
{
    public const int MaxHours = 72;
    /// <summary>TightVNC log level while verbose logging is on (connection and desktop-server events, no per-frame noise).</summary>
    public const int TightVncVerboseLevel = 5;
    /// <summary>TightVNC log level otherwise: errors and warnings, which is what a dying desktop server leaves behind.</summary>
    public const int TightVncNormalLevel = 2;

    public static string FilePath { get; private set; } = @"C:\ProgramData\RemoteAgent\diag.json";
    public static string LogDirectory { get; private set; } = @"C:\ProgramData\RemoteAgent\logs";

    public static void Configure(string dataDirectory)
    {
        FilePath = Path.Combine(dataDirectory, "diag.json");
        LogDirectory = Path.Combine(dataDirectory, "logs");
    }

    /// <summary>When verbose logging ends, or null when it is off: no file, an unreadable one, or one already past its time.</summary>
    public static DateTimeOffset? Until
    {
        get
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllBytes(FilePath));
                if (!doc.RootElement.TryGetProperty("Diag", out var diag) || !diag.TryGetProperty("Until", out var until)) return null;
                return DateTimeOffset.TryParse(until.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var u) ? u : null;
            }
            catch { return null; }
        }
    }

    public static bool IsActive => Until is { } u && u > DateTimeOffset.UtcNow;

    public static int TightVncLogLevel => IsActive ? TightVncVerboseLevel : TightVncNormalLevel;

    /// <summary>Switches verbose logging on until <paramref name="hours"/> from now and returns that time.</summary>
    public static DateTimeOffset Enable(int hours)
    {
        var until = DateTimeOffset.UtcNow.AddHours(Math.Clamp(hours, 1, MaxHours));
        // The Microsoft/System categories stay at Information: their Debug output is framework noise, and the
        // event log keeps its own Information floor from appsettings, so only the file gets the detail.
        var json =
            "{\n" +
            $"  \"Diag\": {{ \"Until\": \"{until.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}\" }},\n" +
            "  \"Logging\": { \"LogLevel\": { \"Default\": \"Debug\", \"Microsoft\": \"Information\", \"System\": \"Information\" } }\n" +
            "}\n";
        // Written whole, then moved into place: the configuration watcher must never read half a file.
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
        return until;
    }

    /// <summary>Switches verbose logging off; the configuration watcher drops the override within a second.</summary>
    public static void Disable()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
