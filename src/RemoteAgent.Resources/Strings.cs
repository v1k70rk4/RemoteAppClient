using System.Globalization;
using System.Resources;

namespace RemoteAgent.Resources;

/// <summary>
/// Typed access to localized strings. ResourceManager and .resx are used underneath:
/// Strings.resx is the English neutral base, Strings.hu.resx is the Hungarian satellite.
/// The typed wrapper is handwritten so CLI builds do not depend on a designer file.
///
/// Callers may set <see cref="Culture"/>, for example CultureInfo.CurrentUICulture,
/// or rely on ResourceManager's default culture resolution.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Rm =
        new("RemoteAgent.Resources.Localization.Strings", typeof(Strings).Assembly);

    /// <summary>Overrides the culture used for lookup. Null = CultureInfo.CurrentUICulture.</summary>
    public static CultureInfo? Culture { get; set; }

    private static string Get(string key) => Rm.GetString(key, Culture) ?? key;

    public static string EnrollUsage => Get(nameof(EnrollUsage));
    public static string EnrollMissingToken => Get(nameof(EnrollMissingToken));
    public static string EnrollGeneratingKeys => Get(nameof(EnrollGeneratingKeys));
    public static string EnrollContactingServer => Get(nameof(EnrollContactingServer));
    public static string EnrollSuccess => Get(nameof(EnrollSuccess));
    public static string EnrollServerUnreachable => Get(nameof(EnrollServerUnreachable));

    /// <summary>Formatted enrollment failure message; parameter is the detail.</summary>
    public static string EnrollFailed(string detail) =>
        string.Format(Culture ?? CultureInfo.CurrentUICulture, Get("EnrollFailedFmt"), detail);
}
