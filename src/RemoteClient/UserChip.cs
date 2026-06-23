using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>Topbar user chip: accent gradient avatar (initials) + username / role. Hover highlights;
/// Click signs out. See design_handoff_console_redesign.</summary>
public sealed class UserChip : Control
{
    private string _name = "";
    private string _role = "";
    private string _initials = "?";
    private bool _hover;

    public UserChip(string username, string role)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(180, 40);
        Cursor = Cursors.Hand;
        SetUser(username, role);
    }

    public void SetUser(string username, string role)
    {
        _name = string.IsNullOrWhiteSpace(username) ? "—" : username.Trim();
        _role = role ?? "";
        _initials = Initials(_name);
        Invalidate();
    }

    private static string Initials(string name)
    {
        var parts = name.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return char.ToUpperInvariant(parts[0][0]).ToString();
        return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}";
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? ThemeManager.Panel);
        if (_hover) UiPaint.FillRoundedRect(g, new Rectangle(0, 2, Width - 1, Height - 4), 9, ThemeManager.Panel2);

        var av = new Rectangle(4, (Height - 32) / 2, 32, 32);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var grad = new LinearGradientBrush(av, ThemeManager.Accent, ThemeManager.Accent2, 55f))
            g.FillEllipse(grad, av);
        TextRenderer.DrawText(g, _initials, UiFont.BodySemi, av, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        int tx = av.Right + 9;
        TextRenderer.DrawText(g, _name, UiFont.BodySemi, new Rectangle(tx, 5, Width - tx - 6, 16),
            ThemeManager.Text, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, _role, UiFont.Small, new Rectangle(tx, 21, Width - tx - 6, 14),
            ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}
