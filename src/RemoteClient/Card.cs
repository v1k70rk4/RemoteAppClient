using System.Drawing;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>
/// Rounded detail card (Panel fill, hairline border) with an optional bold title and a muted description,
/// hosting a single content control below the header. Unlike <see cref="CardPanel"/> it does not dock
/// itself, so callers can stack several at a fixed width. See design_handoff_console_redesign.
/// </summary>
public sealed class Card : Panel
{
    private readonly string? _title;
    private readonly string? _desc;
    private readonly int _bodyHeight;
    private int _descH;

    /// <param name="bodyHeight">When &gt; 0, the card sizes its own height to fit the (possibly multi-line)
    /// description plus this content height — so long descriptions never clip or crowd the body. When 0 the
    /// caller's fixed Height and the legacy single-line header are kept (no layout change for existing cards).</param>
    public Card(string? title, string? desc, Control content, int bodyHeight = 0)
    {
        _title = title; _desc = desc; _bodyHeight = bodyHeight;
        DoubleBuffered = true;
        BackColor = ThemeManager.Bg;   // outside the rounded corners reads as the page bg
        int top = title is null ? 16 : desc is null ? 46 : 66;
        Padding = new Padding(18, top, 18, 16);
        content.Dock = DockStyle.Fill;
        content.BackColor = ThemeManager.Panel;
        Controls.Add(content);
    }

    private int DescTop => _title is null ? 16 : 37;

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_desc is null || Width <= 36) return;
        _descH = TextRenderer.MeasureText(_desc, UiFont.Small, new Size(Width - 36, 0),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
        if (_bodyHeight <= 0) return;   // opt-in: legacy cards keep their fixed header/height
        int top = Math.Max(66, DescTop + _descH + 13);   // grow the header to fit a 2-3 line description
        if (Padding.Top != top) Padding = new Padding(18, top, 18, 16);
        int total = top + _bodyHeight + 16;
        if (Height != total) Height = total;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        var g = e.Graphics;
        UiPaint.DrawCard(g, new Rectangle(0, 0, Width - 1, Height - 1), 12, ThemeManager.Panel, ThemeManager.BorderSoft);
        if (_title is not null)
            TextRenderer.DrawText(g, _title, UiFont.BodySemi, new Rectangle(18, 15, Width - 36, 18),
                ThemeManager.Text, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        if (_desc is not null)
            TextRenderer.DrawText(g, _desc, UiFont.Small, new Rectangle(18, DescTop, Width - 36, (_descH > 0 ? _descH : 26) + 2),
                ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
    }
}
