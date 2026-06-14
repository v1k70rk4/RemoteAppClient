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

    public static string SupervisorWorker_AgentHungHeartbeatAbout0 => Get(nameof(SupervisorWorker_AgentHungHeartbeatAbout0));
    public static string SupervisorWorker_RemoteAgentIsNotRunningState => Get(nameof(SupervisorWorker_RemoteAgentIsNotRunningState));
    public static string SupervisorWorker_AgentStoppedRestarted => Get(nameof(SupervisorWorker_AgentStoppedRestarted));
    public static string SupervisorWorker_AgentStartFailed => Get(nameof(SupervisorWorker_AgentStartFailed));
    public static string SupervisorWorker_TooManyFailedRecoveryAttempts => Get(nameof(SupervisorWorker_TooManyFailedRecoveryAttempts));
    public static string SupervisorWorker_EmptyUpdateReadyNoTarget => Get(nameof(SupervisorWorker_EmptyUpdateReadyNoTarget));
    public static string SupervisorWorker_UpdateDetectedReplacingTarget => Get(nameof(SupervisorWorker_UpdateDetectedReplacingTarget));
    public static string SupervisorWorker_CouldNotReplaceTheExe => Get(nameof(SupervisorWorker_CouldNotReplaceTheExe));
    public static string SupervisorWorker_AgentUpdatedExeReplacement => Get(nameof(SupervisorWorker_AgentUpdatedExeReplacement));
    public static string SupervisorWorker_UpdateAppliedRemoteAgentRestarted => Get(nameof(SupervisorWorker_UpdateAppliedRemoteAgentRestarted));
    public static string SupervisorWorker_ServiceDidNotStopWithin => Get(nameof(SupervisorWorker_ServiceDidNotStopWithin));
    public static string SupervisorWorker_SupervisorCycleError => Get(nameof(SupervisorWorker_SupervisorCycleError));
    public static string SupervisorWorker_KillFailed => Get(nameof(SupervisorWorker_KillFailed));
}
