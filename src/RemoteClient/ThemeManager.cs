using System.Drawing;
using System.Windows.Forms;
using MaterialSkin;

namespace RemoteClient;

/// <summary>
/// Central MaterialSkin theme handling: dark/light switching plus accent color. Forms register
/// themselves with AddFormToManage so changes apply everywhere at once.
/// </summary>
public static class ThemeManager
{
    public static MaterialSkinManager Skin => MaterialSkinManager.Instance;

    // Scheme whose "primary" is the dark design title bar (the MaterialForm caption uses it); the blue is
    // kept as the accent. Buttons opt into the accent (UseAccentColor) so darkening the primary recolors
    // only the caption. MaterialSkin's Primary/Accent enums are int ARGB, so a Color casts straight in.
    private static ColorScheme BuildScheme(bool dark) => new(
        (Primary)Hex(dark ? "#0a1019" : "#1b2a44").ToArgb(),              // primary = title bar
        (Primary)Hex(dark ? "#070b12" : "#152238").ToArgb(),              // dark primary = status strip
        (Primary)Hex("#22324a").ToArgb(),                                 // light primary
        (MaterialSkin.Accent)Hex(dark ? "#4d8df0" : "#276bce").ToArgb(),  // accent = blue
        TextShade.WHITE);

    public static void Init(bool dark)
    {
        Skin.Theme = dark ? MaterialSkinManager.Themes.DARK : MaterialSkinManager.Themes.LIGHT;
        Skin.ColorScheme = BuildScheme(dark);
    }

    public static bool IsDark => Skin.Theme == MaterialSkinManager.Themes.DARK;

    public static void SetDark(bool dark)
    {
        Skin.Theme = dark ? MaterialSkinManager.Themes.DARK : MaterialSkinManager.Themes.LIGHT;
        Skin.ColorScheme = BuildScheme(dark);
    }

    /// <summary>Whether Windows app mode is dark (Personalize\AppsUseLightTheme = 0). Error/unknown -> dark.</summary>
    public static bool IsOsDark()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v ? v == 0 : true;
        }
        catch { return true; }
    }

    /// <summary>Resolves theme mode to dark/light: "light" -> light, "auto" -> OS, otherwise dark.</summary>
    public static bool ResolveDark(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "light" => false,
        "auto" => IsOsDark(),
        _ => true,
    };

    /// <summary>Current theme background color used by MaterialForm.</summary>
    public static Color Background => Skin.BackgroundColor;

    // ---- Design token palette (design_handoff_console_redesign) ------------------------------
    // Resolved per IsDark. Status colors are theme-agnostic (chosen to read on both panels).
    private static Color Hex(string h) => ColorTranslator.FromHtml(h);

    public static Color Bg           => IsDark ? Hex("#0d141d") : Hex("#eceff3");
    public static Color Panel        => IsDark ? Hex("#121c27") : Hex("#ffffff");
    public static Color Panel2       => IsDark ? Hex("#172331") : Hex("#f5f7fa");
    public static Color Panel3       => IsDark ? Hex("#1d2c3c") : Hex("#eef2f6");
    public static Color BorderSoft   => IsDark ? Hex("#23323f") : Hex("#e2e7ee");
    public static Color BorderStrong => IsDark ? Hex("#30414f") : Hex("#d2dae3");
    public static Color Text         => IsDark ? Hex("#e8eef5") : Hex("#1a2533");
    public static Color Text2        => IsDark ? Hex("#9bacbe") : Hex("#5a6878");
    public static Color Text3        => IsDark ? Hex("#637589") : Hex("#8b97a6");
    public static Color Accent       => IsDark ? Hex("#4d8df0") : Hex("#276bce");
    public static Color Accent2      => IsDark ? Hex("#276bce") : Hex("#1f5bb0");
    public static Color AccentSoft   => IsDark ? Color.FromArgb(41, 77, 141, 240) : Color.FromArgb(26, 39, 107, 206);
    public static Color AccentLine   => IsDark ? Color.FromArgb(89, 77, 141, 240) : Color.FromArgb(71, 39, 107, 206);

    // Status roles: foreground + soft (alpha) background. Use over an opaque panel.
    public static Color OkFg     => Hex("#3ecf8e");
    public static Color OkBg     => Color.FromArgb(36, 62, 207, 142);
    public static Color OffFg    => Hex("#8896a8");
    public static Color OffBg    => Color.FromArgb(36, 127, 142, 160);
    public static Color WarnFg   => Hex("#f0b24b");
    public static Color WarnBg   => Color.FromArgb(38, 240, 178, 75);
    public static Color DangerFg => Hex("#f0676b");
    public static Color DangerBg => Color.FromArgb(36, 240, 103, 107);
    public static Color BetaFg   => Hex("#a78bfa");
    public static Color BetaBg   => Color.FromArgb(38, 167, 139, 250);

    /// <summary>Applies current theme background/text colors to a plain ListView.</summary>
    public static void StyleList(ListView list)
    {
        list.BorderStyle = BorderStyle.None;
        list.BackColor = Panel;
        list.ForeColor = Text;
    }

    /// <summary>Applies theme background to a content UserControl and optionally to its ListViews.</summary>
    public static void StyleView(Control view, ListView? list = null)
    {
        view.BackColor = Bg;
        view.ForeColor = Text;
        if (list is not null) StyleList(list);
    }
}
