using System.Drawing;
using System.Windows.Forms;
using MaterialSkin;

namespace RemoteClient;

/// <summary>
/// A MaterialSkin téma központi kezelése: sötét/világos váltás + akcentszín. A formok
/// regisztrálják magukat (AddFormToManage), így a váltás mindenhol egyszerre érvényesül.
/// </summary>
public static class ThemeManager
{
    public static MaterialSkinManager Skin => MaterialSkinManager.Instance;

    private static readonly ColorScheme Scheme = new(
        Primary.Teal700, Primary.Teal900, Primary.Teal400, Accent.Cyan200, TextShade.WHITE);

    public static void Init(bool dark)
    {
        Skin.Theme = dark ? MaterialSkinManager.Themes.DARK : MaterialSkinManager.Themes.LIGHT;
        Skin.ColorScheme = Scheme;
    }

    public static bool IsDark => Skin.Theme == MaterialSkinManager.Themes.DARK;

    public static void SetDark(bool dark) =>
        Skin.Theme = dark ? MaterialSkinManager.Themes.DARK : MaterialSkinManager.Themes.LIGHT;

    /// <summary>Egy sima ListView háttér/szöveg színének igazítása az aktuális témához.</summary>
    public static void StyleList(ListView list)
    {
        list.BorderStyle = BorderStyle.None;
        list.BackColor = IsDark ? Color.FromArgb(45, 45, 48) : Color.White;
        list.ForeColor = IsDark ? Color.Gainsboro : Color.Black;
    }
}
