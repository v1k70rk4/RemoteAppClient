using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>
/// Owner-drawn sidebar navigation item (icon + label, optional count pill) for the redesigned console.
/// Replaces the MaterialButton Contained/Text active swap: active = accent-soft pill + accent text,
/// hover = panel-2. Reads colors from <see cref="ThemeManager"/> at paint time, so a theme switch just
/// needs Invalidate. See design_handoff_console_redesign.
/// </summary>
public sealed class NavItem : Control
{
    private readonly string _icon;
    private bool _hover;
    private bool _active;

    /// <summary>Optional right-aligned mono count pill (e.g. device total on Devices). Null/empty hides it.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? CountText { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => _active;
        set { if (_active != value) { _active = value; Invalidate(); } }
    }

    public NavItem(string icon, string label)
    {
        _icon = icon;
        Text = label;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(220, 34);
        Margin = new Padding(0, 0, 0, 1);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? ThemeManager.Panel);

        var fg = _active ? ThemeManager.Accent : ThemeManager.Text2;
        var rect = new Rectangle(0, 1, Width - 1, Height - 3);
        if (_active) UiPaint.FillRoundedRect(g, rect, 8, ThemeManager.AccentSoft);
        else if (_hover) UiPaint.FillRoundedRect(g, rect, 8, ThemeManager.Panel2);

        UiIcons.Draw(g, _icon, new RectangleF(11, (Height - 18) / 2f, 18, 18), fg);

        int textX = 40, rightPad = 12;
        var font = _active ? UiFont.NavLabelOn : UiFont.NavLabel;

        if (!string.IsNullOrEmpty(CountText))
        {
            var cf = UiFont.MonoSmall;
            var cs = TextRenderer.MeasureText(g, CountText, cf, Size.Empty, TextFormatFlags.NoPadding);
            int pw = cs.Width + 14, ph = 18;
            var pr = new Rectangle(Width - pw - 10, (Height - ph) / 2, pw, ph);
            UiPaint.FillRoundedRect(g, pr, ph / 2f, ThemeManager.Panel3);
            TextRenderer.DrawText(g, CountText, cf, pr, ThemeManager.Text2,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            rightPad = pw + 18;
        }

        TextRenderer.DrawText(g, Text, font, new Rectangle(textX, 0, Width - textX - rightPad, Height), fg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}
