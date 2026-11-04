using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>
/// Devices stat card: uppercase caption + large mono number on a rounded panel card. Number color carries
/// the status role (online green, offline muted, pending amber). See design_handoff_console_redesign.
/// A card is also a filter button: clicking it narrows the list to what it counts, and the card that is the
/// list's current filter is drawn with the accent border (<see cref="Selected"/>).
/// </summary>
public sealed class StatCard : Control
{
    private string _value = "0";
    private Color _valueColor;
    private bool _selected, _hover;

    public StatCard(string caption)
    {
        Text = caption;
        _valueColor = ThemeManager.Text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Height = 60;
        Cursor = Cursors.Hand;
        // A filter button is reached with Tab and pressed with Space or Enter as well as the mouse.
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { e.Handled = true; OnClick(EventArgs.Empty); }
        base.OnKeyDown(e);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color ValueColor { get => _valueColor; set { _valueColor = value; Invalidate(); } }

    /// <summary>The list is filtered to what this card counts.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => _selected; set { if (_selected != value) { _selected = value; Invalidate(); } } }

    public void SetValue(int value) { _value = value.ToString(); Invalidate(); }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? ThemeManager.Bg);
        var fill = _selected ? ThemeManager.AccentSoft : _hover ? ThemeManager.Panel2 : ThemeManager.Panel;
        var border = _selected ? ThemeManager.Accent : _hover ? ThemeManager.BorderStrong : ThemeManager.BorderSoft;
        UiPaint.DrawCard(g, new Rectangle(0, 0, Width - 1, Height - 1), 12, fill, border, _selected ? 1.5f : 1f);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, new Rectangle(4, 4, Width - 9, Height - 9), ThemeManager.Text, fill);
        TextRenderer.DrawText(g, Text.ToUpperInvariant(), UiFont.Label, new Rectangle(15, 12, Width - 30, 14),
            ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, _value, UiFont.StatNumber, new Rectangle(13, 26, Width - 26, 30),
            _valueColor, TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }
}
