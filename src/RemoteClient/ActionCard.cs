using System.Drawing;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>
/// A small action card for the Commands tab: bold title + muted description + a button. Rounded panel card
/// on a Bg-colored parent. See design_handoff_console_redesign (device commands).
/// </summary>
public sealed class ActionCard : Panel
{
    private readonly string _title;
    private readonly string _desc;

    public ActionCard(string title, string desc, Control button)
    {
        _title = title;
        _desc = desc;
        DoubleBuffered = true;
        BackColor = ThemeManager.Bg;   // corners outside the rounded card read as the page bg
        Margin = new Padding(0, 0, 14, 14);
        button.Location = new Point(18, 60);
        Controls.Add(button);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        var g = e.Graphics;
        UiPaint.DrawCard(g, new Rectangle(0, 0, Width - 1, Height - 1), 12, ThemeManager.Panel, ThemeManager.BorderSoft);
        TextRenderer.DrawText(g, _title, UiFont.BodySemi, new Rectangle(18, 16, Width - 36, 18), ThemeManager.Text,
            TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, _desc, UiFont.Small, new Rectangle(18, 36, Width - 36, 18), ThemeManager.Text2,
            TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}
