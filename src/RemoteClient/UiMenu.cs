using System.Drawing;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>
/// Dark context menus / dropdown chrome. A renderer that FORCES the colors (overriding the background paints
/// rather than relying on the ProfessionalColorTable, which the system visual styles can ignore) so menus
/// stay dark regardless of the OS theme. Getters read ThemeManager live, so a dark/light toggle is reflected.
/// Shared via <see cref="Dark"/> and reused by UiCombo popups. See design_handoff_console_redesign.
/// </summary>
public static class UiMenu
{
    public static ToolStripRenderer Dark { get; } = new DarkRenderer();

    public static ContextMenuStrip Themed() =>
        new() { Renderer = Dark, BackColor = ThemeManager.Panel, ForeColor = ThemeManager.Text };

    private sealed class DarkRenderer() : ToolStripProfessionalRenderer(new DarkColors())
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(ThemeManager.Panel);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(ThemeManager.Panel);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var r = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
            using var b = new SolidBrush(e.Item.Selected && e.Item.Enabled ? ThemeManager.Panel2 : ThemeManager.Panel);
            e.Graphics.FillRectangle(b, r);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? ThemeManager.Text : ThemeManager.Text3;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var p = new Pen(ThemeManager.BorderSoft);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color MenuBorder => ThemeManager.BorderStrong;
        public override Color ToolStripBorder => ThemeManager.BorderStrong;
    }
}
