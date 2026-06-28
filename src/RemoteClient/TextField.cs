using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>
/// Boxed text input: a rounded panel (accent border on focus) wrapping a borderless TextBox, with an
/// optional leading glyph. The native TextBox handles caret/selection/IME; we only paint the chrome.
/// Used for search boxes and form fields in the redesign. See design_handoff_console_redesign.
/// </summary>
public sealed class TextField : Control
{
    private readonly TextBox _box = new() { BorderStyle = BorderStyle.None };
    private readonly string? _icon;
    private readonly bool _multiline;
    private bool _focus;

    public TextField(string placeholder = "", int width = 360, bool mono = false, string? icon = null, bool password = false, bool multiline = false)
    {
        _icon = icon;
        _multiline = multiline;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Width = width;
        Height = multiline ? 92 : 38;
        _box.PlaceholderText = placeholder;
        _box.BackColor = ThemeManager.Panel;
        _box.ForeColor = ThemeManager.Text;
        _box.Font = mono ? UiFont.Mono : UiFont.Body;
        _box.UseSystemPasswordChar = password;
        if (multiline) { _box.Multiline = true; _box.ScrollBars = ScrollBars.Vertical; _box.AcceptsReturn = true; } // Enter = newline, not the form's default button
        _box.TextChanged += (_, e) => { Changed?.Invoke(this, EventArgs.Empty); OnTextChanged(e); };
        _box.GotFocus += (_, _) => { _focus = true; Invalidate(); };
        _box.LostFocus += (_, _) => { _focus = false; Invalidate(); };
        // MaterialSkin re-themes every control on its managed forms when an auxiliary MaterialForm (file
        // manager / session panel) opens, resetting the inner TextBox to a light input color — re-assert ours.
        _box.BackColorChanged += (_, _) => { if (_box.BackColor != ThemeManager.Panel) _box.BackColor = ThemeManager.Panel; };
        _box.ForeColorChanged += (_, _) => { if (_box.ForeColor != ThemeManager.Text) _box.ForeColor = ThemeManager.Text; };
        Controls.Add(_box);
    }

    public event EventHandler? Changed;

    /// <summary>Proxies to the inner TextBox so this drops in for MaterialTextBox2 (.Text / TextChanged work).</summary>
    [AllowNull]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public override string Text { get => _box.Text; set => _box.Text = value; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Value { get => _box.Text; set => _box.Text = value; }
    public string Query => _box.Text.Trim();

    /// <summary>Sets the placeholder shown when the field is empty (e.g. a dynamic "leave empty to keep" hint).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Placeholder { set => _box.PlaceholderText = value; }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_multiline) { _box.SetBounds(12, 9, Width - 24, Height - 18); return; }
        int left = _icon is null ? 12 : 36;
        _box.SetBounds(left, (Height - _box.PreferredHeight) / 2, Width - left - 12, _box.PreferredHeight);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); _box.Focus(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? ThemeManager.Bg);
        UiPaint.DrawCard(g, new Rectangle(0, 0, Width - 1, Height - 1), 9, ThemeManager.Panel, _focus ? ThemeManager.Accent : ThemeManager.BorderStrong);
        if (_icon is not null)
            UiIcons.Draw(g, _icon, new RectangleF(12, Height / 2f - 8, 16, 16), ThemeManager.Text3);
    }
}
