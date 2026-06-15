using System.Drawing;
using MaterialSkin.Controls;

namespace RemoteClient.Views;

/// <summary>Shared DPI-safe layout helpers for content views (AutoSize rows and buttons).</summary>
internal static class ViewUi
{
    /// <summary>Toolbar button with AutoSize so text/height scale with DPI.</summary>
    public static MaterialButton ToolbarButton(string text, bool primary = true)
    {
        var b = new MaterialButton { Text = text, AutoSize = true, Margin = new Padding(4, 0, 4, 0) };
        if (!primary) { b.Type = MaterialButton.MaterialButtonType.Outlined; b.HighEmphasis = false; }
        return b;
    }

    /// <summary>AutoSize toolbar row; wraps when it cannot fit in small windows or high DPI.</summary>
    public static FlowLayoutPanel Toolbar() =>
        new() { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(6, 8, 6, 4) };

    /// <summary>
    /// Auto-sizes every column to the larger of its header text and its content, capped so a long
    /// value cannot stretch the table into a horizontal scrollbar. Call after the list is populated.
    /// For selective sizing, set an individual column's Width = -1 (content) or -2 (header) instead.
    /// </summary>
    public static void AutoSizeColumns(ListView list, int max = 600)
    {
        if (list.Columns.Count == 0) return;
        list.BeginUpdate();
        foreach (ColumnHeader col in list.Columns)
        {
            col.Width = -2; int header = col.Width;   // fit the header text
            col.Width = -1; int content = col.Width;  // fit the widest item
            int w = Math.Max(header, content);
            col.Width = max > 0 ? Math.Min(w, max) : w;
        }
        list.EndUpdate();
    }

    /// <summary>AutoSize status row around a MaterialLabel, with height scaling by DPI.</summary>
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
    /// Vertical TableLayoutPanel: every row is AutoSize except the configured fill row (100%).
    /// Controls are added in the given order (0..n), DPI-safely.
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
