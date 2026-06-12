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

    // Az alkalmazás-ikon kékjéhez (~#276BCE) illesztett Material Blue séma.
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

    /// <summary>Az aktuális téma háttérszíne (a MaterialForm rajzolt háttere).</summary>
    public static Color Background => Skin.BackgroundColor;

    /// <summary>Egy sima ListView háttér/szöveg színének igazítása az aktuális témához.</summary>
    public static void StyleList(ListView list)
    {
        list.BorderStyle = BorderStyle.None;
        list.BackColor = IsDark ? Color.FromArgb(45, 45, 48) : Color.White;
        list.ForeColor = IsDark ? Color.Gainsboro : Color.Black;
    }

    /// <summary>Egy tartalom-nézet (UserControl) hátterének a témához igazítása + opcionálisan a benne lévő ListView.</summary>
    public static void StyleView(Control view, ListView? list = null)
    {
        view.BackColor = Skin.BackgroundColor;
        view.ForeColor = IsDark ? Color.Gainsboro : Color.Black;
        if (list is not null) StyleList(list);
    }
}
