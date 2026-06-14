using System.Collections.Generic;
using System.Globalization;

namespace RemoteAgent.Updater.Localization;

internal static partial class Strings
{
    public const string English = "en";
    public const string Hungarian = "hu";

    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> TranslationSource = new(() => new()
    {
        [Hungarian] = Hu,
        [English] = En,
    });

    private static Dictionary<string, Dictionary<string, string>> Translations => TranslationSource.Value;

    private static string? _language;

    public static string Language
    {
        get => ResolveLanguage(_language ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        set => _language = ResolveLanguage(value);
    }

    public static IReadOnlyList<string> AvailableLanguages { get; } = new[] { English, Hungarian };

    public static string GetDisplayName(string langCode) => NormalizeLanguageCode(langCode) switch
    {
        Hungarian => "Magyar",
        English => "English",
        _ => langCode,
    };

    public static string Get(string key)
    {
        if (Translations.TryGetValue(Language, out var dict) && dict.TryGetValue(key, out var value))
            return value;
        if (Translations.TryGetValue(English, out var fallback) && fallback.TryGetValue(key, out var fb))
            return fb;
        return key;
    }

    public static string Format(string format, params object?[] args) =>
        string.Format(CultureInfo.CurrentUICulture, format, args);

    private static string ResolveLanguage(string? langCode)
    {
        var normalized = NormalizeLanguageCode(langCode);
        return Translations.ContainsKey(normalized) ? normalized : English;
    }

    private static string NormalizeLanguageCode(string? langCode)
    {
        if (string.IsNullOrWhiteSpace(langCode)) return string.Empty;

        var normalized = langCode.Trim().ToLowerInvariant();
        var dash = normalized.IndexOf('-');
        return dash > 0 ? normalized[..dash] : normalized;
    }

    public static string SupervisorWorker_001 => Get(nameof(SupervisorWorker_001));
    public static string SupervisorWorker_002 => Get(nameof(SupervisorWorker_002));
    public static string SupervisorWorker_003 => Get(nameof(SupervisorWorker_003));
    public static string SupervisorWorker_004 => Get(nameof(SupervisorWorker_004));
    public static string SupervisorWorker_005 => Get(nameof(SupervisorWorker_005));
    public static string SupervisorWorker_006 => Get(nameof(SupervisorWorker_006));
    public static string SupervisorWorker_007 => Get(nameof(SupervisorWorker_007));
    public static string SupervisorWorker_008 => Get(nameof(SupervisorWorker_008));
    public static string SupervisorWorker_009 => Get(nameof(SupervisorWorker_009));
    public static string SupervisorWorker_010 => Get(nameof(SupervisorWorker_010));
    public static string SupervisorWorker_011 => Get(nameof(SupervisorWorker_011));
    public static string SupervisorWorker_012 => Get(nameof(SupervisorWorker_012));
    public static string SupervisorWorker_013 => Get(nameof(SupervisorWorker_013));
}
