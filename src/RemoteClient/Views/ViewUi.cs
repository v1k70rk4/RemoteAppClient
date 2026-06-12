using System.Drawing;
using MaterialSkin.Controls;

namespace RemoteClient.Views;

/// <summary>Közös, DPI-biztos elrendezés-segédek a tartalom-nézetekhez (AutoSize sorok + AutoSize gombok).</summary>
internal static class ViewUi
{
    /// <summary>Eszköztár-gomb: AutoSize (a szöveg/magasság a DPI-vel skálázódik).</summary>
    public static MaterialButton ToolbarButton(string text, bool primary = true)
    {
        var b = new MaterialButton { Text = text, AutoSize = true, Margin = new Padding(4, 0, 4, 0) };
        if (!primary) { b.Type = MaterialButton.MaterialButtonType.Outlined; b.HighEmphasis = false; }
        return b;
    }

    /// <summary>AutoSize eszköztár-sor; ha nem fér ki (kis ablak / nagy DPI), tördel.</summary>
    public static FlowLayoutPanel Toolbar() =>
        new() { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(6, 8, 6, 4) };

    /// <summary>AutoSize státusz-sor egy MaterialLabel köré (a magasság a DPI-vel skálázódik).</summary>
    public static Panel StatusHost(MaterialLabel status)
    {
        status.AutoSize = true; status.Dock = DockStyle.Top; status.Padding = new Padding(12, 6, 12, 6);
        var host = new Panel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        host.Controls.Add(new MaterialDivider { Dock = DockStyle.Top });
        host.Controls.Add(status);
        status.BringToFront();
        return host;
    }

    /// <summary>
    /// Függőleges TableLayoutPanel: minden sor AutoSize, kivéve a megadott „fill" indexű sort (100%).
    /// A vezérlőket a megadott sorrendben rakja be (0..n), DPI-biztosan.
    /// </summary>
    public static TableLayoutPanel Rows(int fillRow, params Control[] controls)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = controls.Length, Margin = new Padding(0), Padding = new Padding(0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < controls.Length; i++)
        {
            t.RowStyles.Add(new RowStyle(i == fillRow ? SizeType.Percent : SizeType.AutoSize, i == fillRow ? 100 : 0));
            controls[i].Dock = DockStyle.Fill;
            t.Controls.Add(controls[i], 0, i);
        }
        return t;
    }
}
