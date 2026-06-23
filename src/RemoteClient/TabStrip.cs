using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>
/// Owner-drawn tab strip: a row of text tabs with a 2px accent underline + accent text on the active tab,
/// muted text otherwise, and a hairline bottom border. Replaces the MaterialButton Contained/Text tab swap.
/// See design_handoff_console_redesign (device detail tabs).
/// </summary>
public sealed class TabStrip : Control
{
    private (string Key, string Label)[] _tabs = Array.Empty<(string, string)>();
    private string _active = "";
    private readonly List<Rectangle> _rects = new();

    public event Action<string>? TabSelected;

    public TabStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Dock = DockStyle.Top;
        Height = 42;
        Cursor = Cursors.Hand;
    }

    public void SetTabs((string Key, string Label)[] tabs, string active)
    {
        _tabs = tabs;
        _active = active;
        Invalidate();
    }

    public void SetActive(string key)
    {
        if (_active == key) return;
        _active = key;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        for (int i = 0; i < _rects.Count && i < _tabs.Length; i++)
            if (_rects[i].Contains(e.Location)) { TabSelected?.Invoke(_tabs[i].Key); return; }
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? ThemeManager.Bg);
        using (var pen = new Pen(ThemeManager.BorderSoft)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);

        _rects.Clear();
        int x = 2;
        foreach (var (key, label) in _tabs)
        {
            bool active = key == _active;
            var font = active ? UiFont.BodySemi : UiFont.Body;
            var ts = TextRenderer.MeasureText(g, label, font, Size.Empty, TextFormatFlags.NoPadding);
            int w = ts.Width + 30;
            var r = new Rectangle(x, 0, w, Height);
            _rects.Add(r);
            TextRenderer.DrawText(g, label, font, r, active ? ThemeManager.Accent : ThemeManager.Text2,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (active)
                using (var ab = new SolidBrush(ThemeManager.Accent)) g.FillRectangle(ab, x + 6, Height - 2, w - 12, 2);
            x += w;
        }
    }
}
