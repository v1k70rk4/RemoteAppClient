using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>
/// Owner-drawn dropdown: a boxed display (rounded panel + chevron) with a themed popup list. A drop-in for
/// MaterialComboBox in the redesign — exposes <see cref="Items"/> (Add/AddRange/Clear/Count/indexer),
/// <see cref="SelectedIndex"/>/<see cref="SelectedItem"/> and <see cref="SelectedIndexChanged"/>. The popup
/// is a theme-painted owner-drawn ListBox hosted in a ToolStripDropDown (native scroll + keyboard + auto-close).
/// See design_handoff_console_redesign.
/// </summary>
public sealed class UiCombo : Control
{
    private readonly List<object> _items = new();
    private int _selected = -1;
    private bool _hover;
    private bool _open;
    private ToolStripDropDown? _dd;
    private ListBox? _lb;

    public UiCombo(int width = 240)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Width = width;
        Height = 38;
        Cursor = Cursors.Hand;
    }

    /// <summary>Item store (objects rendered by ToString), mirroring ComboBox.Items usage.</summary>
    public List<object> Items => _items;

    public event EventHandler? SelectedIndexChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            int v = _items.Count == 0 ? -1 : Math.Clamp(value, -1, _items.Count - 1);
            if (_selected == v) return;
            _selected = v;
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public object? SelectedItem
    {
        get => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;
        set { int i = value is null ? -1 : _items.IndexOf(value); if (i >= 0) SelectedIndex = i; }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e) { base.OnClick(e); ToggleDropDown(); }

    private void ToggleDropDown()
    {
        if (_open) { _dd?.Close(); return; }
        if (_items.Count == 0) return;
        if (_dd is null) BuildDropDown();

        _lb!.BeginUpdate();
        _lb.Items.Clear();
        foreach (var it in _items) _lb.Items.Add(it);
        _lb.EndUpdate();
        _lb.SelectedIndex = _selected >= 0 && _selected < _items.Count ? _selected : 0;

        int rows = Math.Min(_items.Count, 9);
        var sz = new Size(Math.Max(Width, 160), rows * 32 + 2);
        _lb.Size = sz;
        var host = (ToolStripControlHost)_dd!.Items[0];
        host.AutoSize = false;
        host.Size = sz;
        _dd.Size = new Size(sz.Width + 2, sz.Height + 2); // AutoSize=false needs an explicit size each open

        _open = true;
        _dd.Show(this, new Point(0, Height + 2));
        _lb.Focus();
    }

    // The dropdown is built once and reused — disposing it in its own Closed event leaves the
    // ToolStripManager's modal filter holding a disposed reference (ObjectDisposedException on the next click).
    private void BuildDropDown()
    {
        _lb = new ListBox
        {
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 32, BorderStyle = BorderStyle.None,
            BackColor = ThemeManager.Panel, IntegralHeight = false, Font = UiFont.Body,
        };
        _lb.DrawItem += DrawPopupItem;
        _lb.Click += (_, _) => Pick();
        _lb.KeyDown += (_, ke) => { if (ke.KeyCode == Keys.Enter) Pick(); else if (ke.KeyCode == Keys.Escape) _dd?.Close(); };

        var host = new ToolStripControlHost(_lb) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false };
        _dd = new ToolStripDropDown { Padding = Padding.Empty, AutoSize = false, BackColor = ThemeManager.Panel, Renderer = UiMenu.Dark };
        _dd.Items.Add(host);
        _dd.Closed += (_, _) => { _open = false; Invalidate(); };
    }

    private void Pick()
    {
        if (_lb!.SelectedIndex >= 0) SelectedIndex = _lb.SelectedIndex;
        _dd?.Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _dd?.Dispose(); _dd = null; }
        base.Dispose(disposing);
    }

    private static void DrawPopupItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ListBox lb || e.Index < 0) return;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(sel ? ThemeManager.Panel2 : ThemeManager.Panel)) e.Graphics.FillRectangle(bg, e.Bounds);
        var r = new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 18, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, lb.Items[e.Index].ToString(), UiFont.Body, r, sel ? ThemeManager.Text : ThemeManager.Text2,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? ThemeManager.Bg);
        UiPaint.DrawCard(g, new Rectangle(0, 0, Width - 1, Height - 1), 9, ThemeManager.Panel, _hover || _open ? ThemeManager.BorderStrong : ThemeManager.BorderSoft);
        TextRenderer.DrawText(g, SelectedItem?.ToString() ?? "", UiFont.Body, new Rectangle(12, 0, Width - 40, Height), ThemeManager.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        UiIcons.Draw(g, "chevrondown", new RectangleF(Width - 26, Height / 2f - 8, 16, 16), ThemeManager.Text3);
    }
}
