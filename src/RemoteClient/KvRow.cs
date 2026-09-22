using System.Drawing;
using System.Windows.Forms;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient;

/// <summary>
/// Owner-drawn key/value row for detail cards: label on the left (text-2), value on the right
/// (mono or body, colored), with a hairline divider below. The parent sizes its Width.
/// Clicking the value copies it to the clipboard. Only the value text is the target: a click that merely
/// brings the window forward must not replace what the operator had on the clipboard.
/// See design_handoff_console_redesign (device telemetry / general).
/// </summary>
public sealed class KvRow : Control
{
    private const string Placeholder = "—";   // what the hosts pass for "no value": nothing to copy

    private readonly string _label;
    private string _value;
    private Color _valueColor;
    private Font _valueFont;
    private int _valueWidth = -1;              // measured on demand; -1 = stale
    private bool _hover;
    private string? _flash;                    // shown in place of the value for a moment after a click
    private Color _flashColor;
    private System.Windows.Forms.Timer? _flashTimer;

    public KvRow(string label, string value, Color valueColor, Font valueFont)
    {
        _label = label;
        _value = value;
        _valueColor = valueColor;
        _valueFont = valueFont;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Height = 38;
        Margin = new Padding(0);
    }

    public string Label => _label;

    /// <summary>Swaps the value in place, so a live refresh neither rebuilds the row nor loses a click on it.</summary>
    public void SetValue(string value, Color valueColor, Font valueFont)
    {
        if (value == _value && valueColor == _valueColor && ReferenceEquals(valueFont, _valueFont)) return;
        _value = value;
        _valueColor = valueColor;
        _valueFont = valueFont;
        _valueWidth = -1;
        if (_hover) SetHover(CanCopy && ValueBounds().Contains(PointToClient(MousePosition)));
        Invalidate();
    }

    // Narrow hosts (e.g. the VNC session side panel) can't fit label + value side by side, so the row
    // grows taller and stacks the value under the label instead of clipping it.
    private bool Narrow => Width > 0 && Width < 360;

    private bool CanCopy => _value.Length > 0 && _value != Placeholder;

    private Rectangle ValueArea()
    {
        if (Narrow) return new Rectangle(16, 25, Width - 32, 18);
        int split = (int)(Width * 0.40);
        return new Rectangle(split, 0, Width - split - 16, Height);
    }

    /// <summary>Where the value text actually sits: the click target and the hover highlight.</summary>
    private Rectangle ValueBounds()
    {
        if (_valueWidth < 0)
            _valueWidth = TextRenderer.MeasureText(_value, _valueFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
        var area = ValueArea();
        int w = Math.Max(0, Math.Min(_valueWidth, area.Width));
        int x = Narrow ? area.X : area.Right - w;
        return new Rectangle(x - 5, area.Y + (area.Height - 22) / 2, w + 10, 22);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        int h = Narrow ? 50 : 38;
        if (Height != h) Height = h;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHover(CanCopy && ValueBounds().Contains(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(false);
    }

    private void SetHover(bool on)
    {
        if (_hover == on) return;
        _hover = on;
        Cursor = on ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left || !CanCopy || !ValueBounds().Contains(e.Location)) return;
        bool ok;
        try { Clipboard.SetText(_value); ok = true; }
        catch { ok = false; }   // another process holds the clipboard open (SetText already retried for a second)
        Flash(ok ? L.KvRow_Copied : L.KvRow_CopyFailed, ok ? ThemeManager.OkFg : ThemeManager.DangerFg);
    }

    private void Flash(string text, Color color)
    {
        _flash = text;
        _flashColor = color;
        if (_flashTimer is null)
        {
            var timer = new System.Windows.Forms.Timer { Interval = 1200 };
            timer.Tick += (_, _) => { timer.Stop(); _flash = null; Invalidate(); };
            _flashTimer = timer;
        }
        _flashTimer.Stop();
        _flashTimer.Start();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _flashTimer?.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? ThemeManager.Panel);
        if (_hover && _flash is null) UiPaint.FillRoundedRect(g, ValueBounds(), 6, ThemeManager.Panel3);

        string value = _flash ?? _value;
        var font = _flash is null ? _valueFont : UiFont.Body;
        var color = _flash is null ? _valueColor : _flashColor;
        var area = ValueArea();
        if (Narrow)
        {
            TextRenderer.DrawText(g, _label, UiFont.Label, new Rectangle(16, 7, Width - 32, 15), ThemeManager.Text3,
                TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, value, font, area, color,
                TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
        else
        {
            TextRenderer.DrawText(g, _label, UiFont.Body, new Rectangle(16, 0, area.X - 18, Height), ThemeManager.Text2,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, value, font, area, color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
        using var pen = new Pen(ThemeManager.BorderSoft);
        g.DrawLine(pen, 16, Height - 1, Width - 16, Height - 1);
    }
}
