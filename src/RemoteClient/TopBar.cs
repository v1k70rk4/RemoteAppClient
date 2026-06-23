using System;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>
/// 60px content-area top strip: page title (+ optional subtitle) on the left; a theme toggle and the user
/// chip on the right, separated by a hairline divider. Owner-drawn chrome; hosts the two interactive
/// controls. See design_handoff_console_redesign.
/// </summary>
public sealed class TopBar : Control
{
    private string _title = "";
    private string _subtitle = "";
    private string _status = "";

    public IconButton Toggle { get; }
    public UserChip Chip { get; }

    public TopBar(IconButton toggle, UserChip chip)
    {
        Toggle = toggle;
        Chip = chip;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Top;
        Height = 60;
        BackColor = ThemeManager.Panel;
        Controls.Add(toggle);
        Controls.Add(chip);
    }

    public void SetTitle(string title, string subtitle = "")
    {
        _title = title ?? "";
        _subtitle = subtitle ?? "";
        Invalidate();
    }

    /// <summary>Sets the mono status pill text (e.g. "C2 secure · SSH tunnel"). Empty hides the pill.</summary>
    public void SetStatus(string status) { _status = status ?? ""; Invalidate(); }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Chip.SetBounds(Width - 22 - Chip.Width, (Height - Chip.Height) / 2, Chip.Width, Chip.Height);
        Toggle.SetBounds(Chip.Left - 14 - Toggle.Width, (Height - Toggle.Height) / 2, Toggle.Width, Toggle.Height);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(ThemeManager.Panel);

        // C2 status pill, just left of the theme toggle.
        int leftOfRight = Toggle.Left;
        if (_status.Length > 0)
        {
            var pf = UiFont.MonoSmall;
            var ts = TextRenderer.MeasureText(g, _status, pf, Size.Empty, TextFormatFlags.NoPadding);
            int padX = 11, dotW = 13, ph = 26;
            int pw = padX + dotW + ts.Width + padX;
            int px = Toggle.Left - 16 - pw;
            UiPaint.DrawCard(g, new Rectangle(px, (Height - ph) / 2, pw, ph), ph / 2f, ThemeManager.Panel2, ThemeManager.BorderSoft);
            using (var db = new SolidBrush(ThemeManager.OkFg)) g.FillEllipse(db, px + padX, Height / 2 - 3, 6, 6);
            TextRenderer.DrawText(g, _status, pf, new Rectangle(px + padX + dotW, 0, ts.Width + 4, Height), ThemeManager.Text2,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            leftOfRight = px;
        }

        using (var pen = new Pen(ThemeManager.BorderSoft))
            g.DrawLine(pen, 0, Height - 1, Width, Height - 1);                  // bottom border

        bool hasSub = _subtitle.Length > 0;
        int titleRight = Math.Max(80, leftOfRight - 44);
        TextRenderer.DrawText(g, _title, UiFont.PageTitle,
            new Rectangle(22, hasSub ? 9 : 0, titleRight, hasSub ? 24 : Height),
            ThemeManager.Text,
            TextFormatFlags.Left | (hasSub ? TextFormatFlags.Bottom : TextFormatFlags.VerticalCenter) | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        if (hasSub)
            TextRenderer.DrawText(g, _subtitle, UiFont.Small, new Rectangle(22, 34, titleRight, 16),
                ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}
