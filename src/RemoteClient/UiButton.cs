using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>
/// Owner-drawn button with style variants (filled accent / outline / danger) and an optional leading glyph.
/// Used where the design needs an icon or a danger color (header Connect/Files, command cards); plain forms
/// keep MaterialButton. See design_handoff_console_redesign.
/// </summary>
public sealed class UiButton : Control, IButtonControl
{
    public enum Style { Filled, Outline, Danger, Warn }

    private readonly Style _style;
    private readonly string? _icon;
    private bool _hover;

    public UiButton(string text, Style style = Style.Filled, string? icon = null)
    {
        _style = style;
        _icon = icon;
        Text = text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
        Height = 38;
        var ts = TextRenderer.MeasureText(text, UiFont.BodySemi);
        Width = 16 + (icon is null ? 0 : 24) + ts.Width + 18;
    }

    // IButtonControl: lets a form use this as AcceptButton (Enter submits).
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DialogResult DialogResult { get; set; } = DialogResult.None;
    public void NotifyDefault(bool value) { }
    public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var parentBg = Parent?.BackColor ?? ThemeManager.Panel;
        g.Clear(parentBg);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);

        Color fill, border, fg;
        switch (_style)
        {
            case Style.Filled:
                fill = _hover ? Lighten(ThemeManager.Accent) : ThemeManager.Accent; border = fill; fg = Color.White; break;
            case Style.Danger:
                fill = _hover ? ThemeManager.DangerBg : parentBg; border = ThemeManager.DangerFg; fg = ThemeManager.DangerFg; break;
            case Style.Warn:
                fill = _hover ? ThemeManager.WarnBg : parentBg; border = ThemeManager.WarnFg; fg = ThemeManager.WarnFg; break;
            default:
                fill = _hover ? ThemeManager.Panel2 : parentBg; border = ThemeManager.BorderStrong; fg = ThemeManager.Text; break;
        }
        if (!Enabled) { fg = ThemeManager.Text3; fill = _style == Style.Filled ? ThemeManager.Panel3 : parentBg; border = ThemeManager.BorderSoft; }
        UiPaint.DrawCard(g, r, 9, fill, border);

        // Center the label (icon + text as a group) so wide buttons read as centered actions, per the handoff.
        if (_icon is null)
        {
            TextRenderer.DrawText(g, Text, UiFont.BodySemi, new Rectangle(8, 0, Width - 16, Height), fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
        else
        {
            int groupW = 22 + TextRenderer.MeasureText(Text, UiFont.BodySemi).Width;   // 16 icon + 6 gap + text
            int startX = Math.Max(12, (Width - groupW) / 2);
            UiIcons.Draw(g, _icon, new RectangleF(startX, Height / 2f - 8, 16, 16), fg);
            TextRenderer.DrawText(g, Text, UiFont.BodySemi, new Rectangle(startX + 22, 0, Width - startX - 22 - 8, Height), fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    private static Color Lighten(Color c, int a = 20) =>
        Color.FromArgb(Math.Min(255, c.R + a), Math.Min(255, c.G + a), Math.Min(255, c.B + a));
}
