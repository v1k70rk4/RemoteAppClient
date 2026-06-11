using System.Globalization;
using System.Resources;

namespace RemoteAgent.Resources;

/// <summary>
/// Lokalizált szövegek típusos elérése. A háttérben ResourceManager + .resx
/// (Strings.resx = en alap, Strings.hu.resx = magyar szatellit). A típusos
/// elérő kézzel írt, hogy CLI-buildből is megbízhatóan forduljon (nincs designer-függés).
///
/// A hívó beállíthatja a <see cref="Culture"/>-t (pl. CultureInfo.CurrentUICulture),
/// vagy bízhatja a ResourceManager alapértelmezett kultúra-feloldására.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Rm =
        new("RemoteAgent.Resources.Localization.Strings", typeof(Strings).Assembly);

    /// <summary>Felülbírálja a használt kultúrát. Null = CultureInfo.CurrentUICulture.</summary>
    public static CultureInfo? Culture { get; set; }

    private static string Get(string key) => Rm.GetString(key, Culture) ?? key;

    public static string EnrollUsage => Get(nameof(EnrollUsage));
    public static string EnrollMissingToken => Get(nameof(EnrollMissingToken));
    public static string EnrollGeneratingKeys => Get(nameof(EnrollGeneratingKeys));
    public static string EnrollContactingServer => Get(nameof(EnrollContactingServer));
    public static string EnrollSuccess => Get(nameof(EnrollSuccess));
    public static string EnrollServerUnreachable => Get(nameof(EnrollServerUnreachable));

    /// <summary>Formázott: a beléptetés sikertelen, paraméter a részlet.</summary>
    public static string EnrollFailed(string detail) =>
        string.Format(Culture ?? CultureInfo.CurrentUICulture, Get("EnrollFailedFmt"), detail);
}
