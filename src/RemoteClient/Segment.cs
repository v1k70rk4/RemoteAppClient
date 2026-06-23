using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>
/// Owner-drawn segmented selector (e.g. Inherited | Yes | No): a rounded Panel3 track with a raised pill on
/// the selected segment. Click a segment to select it. Used for tri-state device flags in the redesigned
/// detail tabs. See design_handoff_console_redesign (permissions).
/// </summary>
public sealed class Segment : Control
{
    private readonly string[] _options;
    private readonly int[] _segX;
    private readonly int[] _segW;
    private int _selected;

    public Segment(params string[] options)
    {
        _options = options.Length > 0 ? options : new[] { "" };
        _segX = new int[_options.Length];
        _segW = new int[_options.Length];
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
        Height = 34;
        int x = 3;
        for (int i = 0; i < _options.Length; i++)
        {
            int w = TextRenderer.MeasureText(_options[i], UiFont.Label).Width + 26;
            _segX[i] = x; _segW[i] = w; x += w;
        }
        Width = x + 3;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            int v = Math.Clamp(value, 0, _options.Length - 1);
            if (_selected == v) return;
            _selected = v; SelectedChanged?.Invoke(this, EventArgs.Empty); Invalidate();
        }
    }

    public event EventHandler? SelectedChanged;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        for (int i = 0; i < _options.Length; i++)
            if (e.X >= _segX[i] && e.X < _segX[i] + _segW[i]) { SelectedIndex = i; break; }
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? ThemeManager.Panel);
        UiPaint.FillRoundedRect(g, new Rectangle(0, 0, Width, Height), 8, ThemeManager.Panel3);
        for (int i = 0; i < _options.Length; i++)
        {
            var r = new Rectangle(_segX[i], 3, _segW[i], Height - 6);
            bool on = i == _selected;
            if (on) UiPaint.FillRoundedRect(g, r, 6, ThemeManager.Panel);
            TextRenderer.DrawText(g, _options[i], UiFont.Label, r, on ? ThemeManager.Text : ThemeManager.Text3,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
