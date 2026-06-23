using System.Drawing;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>
/// Reusable detail card: a panel-colored rounded card (hairline border) with an optional section title;
/// the content control fills the card below the title. Put it on a Bg-colored parent with some padding so
/// the card reads as a card. See design_handoff_console_redesign (device detail panels).
/// </summary>
public sealed class CardPanel : Panel
{
    private readonly string _title;

    public CardPanel(string title, Control content)
    {
        _title = title ?? "";
        DoubleBuffered = true;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Panel;
        Padding = new Padding(2, _title.Length > 0 ? 50 : 2, 2, 2);
        content.Dock = DockStyle.Fill;
        content.BackColor = ThemeManager.Panel;
        Controls.Add(content);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        var g = e.Graphics;
        UiPaint.DrawCard(g, new Rectangle(0, 0, Width - 1, Height - 1), 12, ThemeManager.Panel, ThemeManager.BorderSoft);
        if (_title.Length == 0) return;
        TextRenderer.DrawText(g, _title, UiFont.SectionTitle, new Rectangle(18, 16, Width - 36, 20),
            ThemeManager.Text, TextFormatFlags.Left | TextFormatFlags.NoPadding);
        using var pen = new Pen(ThemeManager.BorderSoft);
        g.DrawLine(pen, 16, 47, Width - 16, 47);
    }
}
