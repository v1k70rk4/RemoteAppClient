using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>
/// Devices stat card: uppercase caption + large mono number on a rounded panel card. Number color carries
/// the status role (online green, offline muted, pending amber). See design_handoff_console_redesign.
/// </summary>
public sealed class StatCard : Control
{
    private string _value = "0";
    private Color _valueColor;

    public StatCard(string caption)
    {
        Text = caption;
        _valueColor = ThemeManager.Text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Height = 60;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color ValueColor { get => _valueColor; set { _valueColor = value; Invalidate(); } }

    public void SetValue(int value) { _value = value.ToString(); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? ThemeManager.Bg);
        UiPaint.DrawCard(g, new Rectangle(0, 0, Width - 1, Height - 1), 12, ThemeManager.Panel, ThemeManager.BorderSoft);
        TextRenderer.DrawText(g, Text.ToUpperInvariant(), UiFont.Label, new Rectangle(15, 12, Width - 30, 14),
            ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, _value, UiFont.StatNumber, new Rectangle(13, 26, Width - 26, 30),
            _valueColor, TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }
}
