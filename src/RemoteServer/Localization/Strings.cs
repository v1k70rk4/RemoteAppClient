using System.Collections.Generic;
using System.Globalization;

namespace RemoteServer.Localization;

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

    public static string CertificateAuthority_001 => Get(nameof(CertificateAuthority_001));
    public static string CertificateAuthority_002 => Get(nameof(CertificateAuthority_002));
    public static string CertificateAuthority_003 => Get(nameof(CertificateAuthority_003));
    public static string CommandService_001 => Get(nameof(CommandService_001));
    public static string CommandService_002 => Get(nameof(CommandService_002));
    public static string CommandService_003 => Get(nameof(CommandService_003));
    public static string CommandSigner_001 => Get(nameof(CommandSigner_001));
    public static string EmailSender_001 => Get(nameof(EmailSender_001));
    public static string EmailSender_002 => Get(nameof(EmailSender_002));
    public static string EmailSender_003 => Get(nameof(EmailSender_003));
    public static string EmailSender_004 => Get(nameof(EmailSender_004));
    public static string EmailSender_005 => Get(nameof(EmailSender_005));
    public static string EmailSender_006 => Get(nameof(EmailSender_006));
    public static string EmailSender_007 => Get(nameof(EmailSender_007));
    public static string EmailSender_008 => Get(nameof(EmailSender_008));
    public static string EmailSender_009 => Get(nameof(EmailSender_009));
    public static string EmailSender_010 => Get(nameof(EmailSender_010));
    public static string EmailSender_011 => Get(nameof(EmailSender_011));
    public static string EnrollmentService_001 => Get(nameof(EnrollmentService_001));
    public static string EnrollmentService_002 => Get(nameof(EnrollmentService_002));
    public static string MsiBuilder_001 => Get(nameof(MsiBuilder_001));
    public static string MsiBuilder_002 => Get(nameof(MsiBuilder_002));
    public static string MsiBuilder_003 => Get(nameof(MsiBuilder_003));
    public static string MsiBuilder_004 => Get(nameof(MsiBuilder_004));
    public static string MsiBuilder_005 => Get(nameof(MsiBuilder_005));
    public static string MsiBuilder_006 => Get(nameof(MsiBuilder_006));
    public static string Program_001 => Get(nameof(Program_001));
    public static string Program_002 => Get(nameof(Program_002));
    public static string Program_003 => Get(nameof(Program_003));
    public static string Program_004 => Get(nameof(Program_004));
    public static string Program_005 => Get(nameof(Program_005));
    public static string Program_006 => Get(nameof(Program_006));
    public static string Program_007 => Get(nameof(Program_007));
    public static string Program_008 => Get(nameof(Program_008));
    public static string Program_009 => Get(nameof(Program_009));
    public static string Program_010 => Get(nameof(Program_010));
    public static string Program_011 => Get(nameof(Program_011));
    public static string Program_012 => Get(nameof(Program_012));
    public static string Program_013 => Get(nameof(Program_013));
    public static string Program_014 => Get(nameof(Program_014));
    public static string Program_015 => Get(nameof(Program_015));
    public static string Program_016 => Get(nameof(Program_016));
    public static string Program_017 => Get(nameof(Program_017));
    public static string Program_018 => Get(nameof(Program_018));
    public static string Program_019 => Get(nameof(Program_019));
    public static string Program_020 => Get(nameof(Program_020));
    public static string Program_021 => Get(nameof(Program_021));
    public static string Program_022 => Get(nameof(Program_022));
    public static string Program_023 => Get(nameof(Program_023));
    public static string Program_024 => Get(nameof(Program_024));
    public static string Program_025 => Get(nameof(Program_025));
    public static string Program_026 => Get(nameof(Program_026));
    public static string Program_027 => Get(nameof(Program_027));
    public static string Program_028 => Get(nameof(Program_028));
    public static string SecretExpiryWatcher_001 => Get(nameof(SecretExpiryWatcher_001));
    public static string SecretExpiryWatcher_002 => Get(nameof(SecretExpiryWatcher_002));
    public static string SecretExpiryWatcher_003 => Get(nameof(SecretExpiryWatcher_003));
    public static string SecretExpiryWatcher_004 => Get(nameof(SecretExpiryWatcher_004));
    public static string SecretExpiryWatcher_005 => Get(nameof(SecretExpiryWatcher_005));
    public static string SecretExpiryWatcher_006 => Get(nameof(SecretExpiryWatcher_006));
    public static string SecretProtector_001 => Get(nameof(SecretProtector_001));
    public static string SecretProtector_002 => Get(nameof(SecretProtector_002));
    public static string SecretProtector_003 => Get(nameof(SecretProtector_003));
    public static string SshCertificateAuthority_001 => Get(nameof(SshCertificateAuthority_001));
    public static string SshCertificateAuthority_002 => Get(nameof(SshCertificateAuthority_002));
}
