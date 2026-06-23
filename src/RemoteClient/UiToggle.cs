using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>
/// Owner-drawn on/off switch: a rounded track with a sliding knob and a label to the right. Reads
/// ThemeManager at paint time; click anywhere to toggle. Replaces MaterialSwitch in the redesigned
/// detail tabs. See design_handoff_console_redesign.
/// </summary>
public sealed class UiToggle : Control
{
    private const int TrackW = 40, TrackH = 22;

    private bool _checked;
    private bool _hover;

    public UiToggle(string text = "")
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Text = text;
        Height = 30;
        Width = string.IsNullOrEmpty(text) ? TrackW + 6 : TrackW + 12 + TextRenderer.MeasureText(text, UiFont.Body).Width;
        Margin = new Padding(4, 6, 4, 6);
        Cursor = Cursors.Hand;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; CheckedChanged?.Invoke(this, EventArgs.Empty); Invalidate(); }
    }

    public event EventHandler? CheckedChanged;

    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? ThemeManager.Panel);

        int ty = (Height - TrackH) / 2;
        var track = new Rectangle(0, ty, TrackW, TrackH);
        if (_checked)
            UiPaint.FillRoundedRect(g, track, TrackH / 2, _hover ? ThemeManager.Accent2 : ThemeManager.Accent);
        else
            UiPaint.DrawCard(g, track, TrackH / 2, _hover ? ThemeManager.Panel2 : ThemeManager.Panel3, ThemeManager.BorderStrong);

        int knob = TrackH - 6;
        int kx = _checked ? track.Right - knob - 3 : track.X + 3;
        using (var b = new SolidBrush(_checked ? Color.White : ThemeManager.Text2))
            g.FillEllipse(b, new Rectangle(kx, ty + 3, knob, knob));

        if (!string.IsNullOrEmpty(Text))
            TextRenderer.DrawText(g, Text, UiFont.Body, new Rectangle(TrackW + 12, 0, Width - TrackW - 12, Height),
                Enabled ? ThemeManager.Text : ThemeManager.Text3,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}
