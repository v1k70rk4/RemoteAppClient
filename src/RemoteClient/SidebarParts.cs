using System.Drawing;
using System.Windows.Forms;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient;

/// <summary>Uppercase section caption in the sidebar nav (FLEET / MANAGE / SYSTEM). Owner-drawn so a theme
/// switch only needs Invalidate. See design_handoff_console_redesign.</summary>
public sealed class NavCaption : Control
{
    public NavCaption(string caption)
    {
        Text = caption.ToUpperInvariant();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(220, 18);
        Margin = new Padding(0, 6, 0, 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? ThemeManager.Panel);
        TextRenderer.DrawText(e.Graphics, Text, UiFont.Label, new Rectangle(11, 0, Width - 14, Height),
            ThemeManager.Text3, TextFormatFlags.Bottom | TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }
}

/// <summary>Sidebar header: rounded accent logo tile (monitor glyph) + product name and "{owner} · Fleet".
/// Owner-drawn. See design_handoff_console_redesign.</summary>
public sealed class SidebarHeader : Control
{
    private readonly string _owner;

    public SidebarHeader(string? ownerName)
    {
        _owner = string.IsNullOrWhiteSpace(ownerName) ? L.MainForm_NavFleet : ownerName!.Trim() + " · " + L.MainForm_NavFleet;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Dock = DockStyle.Top;
        Height = 56;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? ThemeManager.Panel);

        // Accent logo tile with a monitor glyph.
        var tile = new Rectangle(14, (Height - 32) / 2, 32, 32);
        using (var grad = new System.Drawing.Drawing2D.LinearGradientBrush(tile, ThemeManager.Accent, ThemeManager.Accent2, 55f))
        using (var path = UiPaint.RoundedRect(tile, 9))
            g.FillPath(grad, path);
        UiIcons.Draw(g, "monitor", new RectangleF(tile.X + 7, tile.Y + 7, 18, 18), Color.White, 1.7f);

        int tx = tile.Right + 11;
        TextRenderer.DrawText(g, "RemoteAppClient", UiFont.BodySemi, new Rectangle(tx, 11, Width - tx - 8, 18),
            ThemeManager.Text, TextFormatFlags.Left | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, _owner, UiFont.Small, new Rectangle(tx, 29, Width - tx - 8, 16),
            ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}
