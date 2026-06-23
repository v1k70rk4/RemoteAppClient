using System.Drawing;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>
/// Owner-drawn key/value row for detail cards: label on the left (text-2), value on the right
/// (mono or body, colored), with a hairline divider below. The parent sizes its Width.
/// See design_handoff_console_redesign (device telemetry / general).
/// </summary>
public sealed class KvRow : Control
{
    private readonly string _label;
    private readonly string _value;
    private readonly Color _valueColor;
    private readonly Font _valueFont;

    public KvRow(string label, string value, Color valueColor, Font valueFont)
    {
        _label = label;
        _value = value;
        _valueColor = valueColor;
        _valueFont = valueFont;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Height = 38;
        Margin = new Padding(0);
    }

    // Narrow hosts (e.g. the VNC session side panel) can't fit label + value side by side, so the row
    // grows taller and stacks the value under the label instead of clipping it.
    private bool Narrow => Width > 0 && Width < 360;

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        int h = Narrow ? 50 : 38;
        if (Height != h) Height = h;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? ThemeManager.Panel);
        if (Narrow)
        {
            TextRenderer.DrawText(g, _label, UiFont.Label, new Rectangle(16, 7, Width - 32, 15), ThemeManager.Text3,
                TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, _value, _valueFont, new Rectangle(16, 25, Width - 32, 18), _valueColor,
                TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
        else
        {
            int split = (int)(Width * 0.40);
            TextRenderer.DrawText(g, _label, UiFont.Body, new Rectangle(16, 0, split - 18, Height), ThemeManager.Text2,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, _value, _valueFont, new Rectangle(split, 0, Width - split - 16, Height), _valueColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
        using var pen = new Pen(ThemeManager.BorderSoft);
        g.DrawLine(pen, 16, Height - 1, Width - 16, Height - 1);
    }
}
