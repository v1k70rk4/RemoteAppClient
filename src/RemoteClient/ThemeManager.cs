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

    // Material Blue scheme matched to the application icon blue (~#276BCE).
    private static readonly ColorScheme Scheme = new(
        Primary.Blue700, Primary.Blue900, Primary.Blue400, Accent.LightBlue200, TextShade.WHITE);

    public static void Init(bool dark)
    {
        Skin.Theme = dark ? MaterialSkinManager.Themes.DARK : MaterialSkinManager.Themes.LIGHT;
        Skin.ColorScheme = Scheme;
    }

    public static bool IsDark => Skin.Theme == MaterialSkinManager.Themes.DARK;

    public static void SetDark(bool dark) =>
        Skin.Theme = dark ? MaterialSkinManager.Themes.DARK : MaterialSkinManager.Themes.LIGHT;

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

    /// <summary>Applies current theme background/text colors to a plain ListView.</summary>
    public static void StyleList(ListView list)
    {
        list.BorderStyle = BorderStyle.None;
        list.BackColor = IsDark ? Color.FromArgb(45, 45, 48) : Color.White;
        list.ForeColor = IsDark ? Color.Gainsboro : Color.Black;
    }

    /// <summary>Applies theme background to a content UserControl and optionally to its ListViews.</summary>
    public static void StyleView(Control view, ListView? list = null)
    {
        view.BackColor = Skin.BackgroundColor;
        view.ForeColor = IsDark ? Color.Gainsboro : Color.Black;
        if (list is not null) StyleList(list);
    }
}
