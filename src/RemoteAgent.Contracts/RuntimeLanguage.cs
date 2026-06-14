using System.Globalization;
using System.Text.Json;

namespace RemoteAgent.Globalization;

public static class RuntimeLanguage
{
    public const string Auto = "auto";
    public const string English = "en";
    public const string Hungarian = "hu";

    private const string SettingsDirectoryName = "RemoteAppClient";
    private const string SettingsFileName = "settings.json";
    private const string LanguagePropertyName = "language";

    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentCulture;
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;

    public static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            SettingsDirectoryName,
            SettingsFileName);

    public static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return Auto;

        return language.Trim().ToLowerInvariant() switch
        {
            Auto or "system" or "default" => Auto,
            English or "en-us" or "en-gb" => English,
            Hungarian or "hu-hu" => Hungarian,
            _ => Auto,
        };
    }

    public static string LoadPreference()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return Auto;

            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty(LanguagePropertyName, out var language) &&
                language.ValueKind == JsonValueKind.String)
                return Normalize(language.GetString());
        }
        catch
        {
            // Invalid or unreadable shared settings should not prevent any exe from starting.
        }

        return Auto;
    }

    public static void SavePreference(string language)
    {
        var normalized = Normalize(language);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);

        using var stream = File.Create(SettingsPath);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString(LanguagePropertyName, normalized);
        writer.WriteEndObject();
    }

    public static void ApplyFromSharedSettings() => Apply(LoadPreference());

    public static void Apply(string? language)
    {
        var normalized = Normalize(language);
        if (normalized == Auto)
        {
            SetCulture(SystemCulture, SystemUiCulture);
            return;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(normalized == Hungarian ? "hu-HU" : "en-US");
            SetCulture(culture, culture);
        }
        catch (CultureNotFoundException)
        {
            SetCulture(SystemCulture, SystemUiCulture);
        }
    }

    private static void SetCulture(CultureInfo culture, CultureInfo uiCulture)
    {
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = uiCulture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = uiCulture;
    }
}
