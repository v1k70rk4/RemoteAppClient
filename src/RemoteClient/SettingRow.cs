using System;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>
/// A settings row: bold title + muted description on the left, a control (e.g. a toggle) pinned to the
/// right, with a hairline divider underneath. See design_handoff_console_redesign (device permissions).
/// </summary>
public sealed class SettingRow : Control
{
    private readonly string _title;
    private readonly string _desc;
    private readonly Control _right;

    public SettingRow(string title, string desc, Control right)
    {
        _title = title; _desc = desc; _right = right;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Controls.Add(right);
        Height = 56;   // set last so the size-changed handler sees the hosted control
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Controls.Count == 0) return;
        _right.Location = new Point(Width - _right.Width - 2, (Height - _right.Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? ThemeManager.Panel);
        int textW = Width - _right.Width - 14;
        TextRenderer.DrawText(g, _title, UiFont.Body, new Rectangle(0, 9, textW, 18),
            ThemeManager.Text, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        if (_desc.Length > 0)
            TextRenderer.DrawText(g, _desc, UiFont.Small, new Rectangle(0, 29, textW, 16),
                ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        using var pen = new Pen(ThemeManager.BorderSoft);
        g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}
