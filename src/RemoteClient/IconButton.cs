using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>Small square owner-drawn icon button (e.g. the topbar theme toggle). Hover emphasizes the
/// border + glyph; Click fires normally. See design_handoff_console_redesign.</summary>
public sealed class IconButton : Control
{
    private string _icon;
    private bool _hover;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string IconKey { get => _icon; set { if (_icon != value) { _icon = value; Invalidate(); } } }

    public IconButton(string icon)
    {
        _icon = icon;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(36, 36);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? ThemeManager.Panel);
        UiPaint.DrawCard(g, new Rectangle(0, 0, Width - 1, Height - 1), 9,
            ThemeManager.Panel2, _hover ? ThemeManager.BorderStrong : ThemeManager.BorderSoft);
        UiIcons.Draw(g, _icon, new RectangleF(Width / 2f - 9, Height / 2f - 9, 18, 18),
            _hover ? ThemeManager.Text : ThemeManager.Text2);
    }
}
