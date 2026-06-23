using System;
using System.Drawing;
using System.Windows.Forms;
using MaterialSkin.Controls;

namespace RemoteClient;

/// <summary>
/// Device-detail header: back button + device icon tile + hostname (mono) + status pill + subtitle
/// (note · group · os), with Files / Connect (VNC) / overflow buttons on the right. Owner-drawn chrome;
/// hosts the interactive buttons. See design_handoff_console_redesign.
/// </summary>
public sealed class DetailHeader : Control
{
    public IconButton Back { get; } = new("chevron");
    public IconButton More { get; } = new("dots");
    public UiButton Files { get; }
    public UiButton Connect { get; }

    private string _name = "";
    private string _subtitle = "";
    private string _statusText = "";
    private Color _statusFg, _statusBg;

    public DetailHeader(string filesText, string connectText)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Top;
        Height = 80;
        Files = new UiButton(filesText, UiButton.Style.Outline, "folder");
        Connect = new UiButton(connectText, UiButton.Style.Filled, "play");
        Controls.Add(Back);
        Controls.Add(More);
        Controls.Add(Files);
        Controls.Add(Connect);
    }

    public void SetDevice(string name, string subtitle, string statusText, Color statusFg, Color statusBg)
    {
        _name = name; _subtitle = subtitle; _statusText = statusText; _statusFg = statusFg; _statusBg = statusBg;
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Connect is null || Files is null) return;   // a resize can fire before the ctor assigns these
        int cy = Height / 2;
        Back.SetBounds(0, cy - 20, 40, 40);
        More.SetBounds(Width - 40, cy - 20, 40, 40);
        Connect.Location = new Point(More.Left - 12 - Connect.Width, cy - Connect.Height / 2);
        Files.Location = new Point(Connect.Left - 10 - Files.Width, cy - Files.Height / 2);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? ThemeManager.Bg);
        using (var pen = new Pen(ThemeManager.BorderSoft)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);

        int cy = Height / 2;
        var tile = new Rectangle(Back.Right + 14, cy - 22, 44, 44);
        UiPaint.FillRoundedRect(g, tile, 10, ThemeManager.Panel3);
        UiIcons.Draw(g, "monitor", new RectangleF(tile.X + 11, tile.Y + 11, 22, 22), ThemeManager.Text2);

        int tx = tile.Right + 14;
        int rightLimit = Files.Left - 16;
        var ns = TextRenderer.MeasureText(g, _name, UiFont.HostTitle, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, _name, UiFont.HostTitle, new Rectangle(tx, cy - 23, Math.Min(ns.Width, rightLimit - tx), 24),
            ThemeManager.Text, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        if (_statusText.Length > 0)
            UiPaint.DrawPill(g, Math.Min(tx + ns.Width + 12, rightLimit - 80), cy - 11, _statusText, _statusFg, _statusBg, UiFont.Small, dot: true);

        if (_subtitle.Length > 0)
            TextRenderer.DrawText(g, _subtitle, UiFont.Small, new Rectangle(tx, cy + 5, rightLimit - tx, 16),
                ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}
