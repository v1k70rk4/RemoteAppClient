using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>
/// GDI+ drawing helpers for the redesigned console (design_handoff_console_redesign): rounded cards,
/// pill / status badges. WinForms / MaterialSkin can't do rounded radii or soft pills natively, so the
/// owner-drawn views and custom panels paint them through here. Colors come from <see cref="ThemeManager"/>.
/// </summary>
public static class UiPaint
{
    /// <summary>A rounded-rectangle path. radius &lt;= 0 returns a plain rectangle.</summary>
    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0f) { path.AddRectangle(r); path.CloseFigure(); return path; }
        float d = radius * 2f;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Fills a rounded rectangle (anti-aliased).</summary>
    public static void FillRoundedRect(Graphics g, RectangleF r, float radius, Color fill)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = RoundedRect(r, radius))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);
        g.SmoothingMode = old;
    }

    /// <summary>Paints a card: rounded fill + 1px (or bw) hairline border, stroke kept inside the bounds.</summary>
    public static void DrawCard(Graphics g, RectangleF r, float radius, Color fill, Color border, float bw = 1f)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var inner = RectangleF.Inflate(r, -bw / 2f, -bw / 2f);
        using (var path = RoundedRect(inner, radius))
        {
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
            using (var pen = new Pen(border, bw)) g.DrawPath(pen, path);
        }
        g.SmoothingMode = old;
    }

    /// <summary>
    /// Draws a pill badge (optional leading status dot) at x, vertically centered on cy, and returns its
    /// total width so callers can lay out the next element. Uses an alpha-soft background over the panel.
    /// </summary>
    public static int DrawPill(Graphics g, int x, int cy, string text, Color fg, Color bg, Font font, bool dot)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var ts = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding);
        const int padX = 9;
        int dotW = dot ? 12 : 0;
        int h = ts.Height + 8;
        int w = padX + dotW + ts.Width + padX;
        var rect = new Rectangle(x, cy - h / 2, w, h);

        using (var path = RoundedRect(rect, h / 2f))
        using (var brush = new SolidBrush(bg))
            g.FillPath(brush, path);

        int textX = x + padX;
        if (dot)
        {
            using var dotBrush = new SolidBrush(fg);
            g.FillEllipse(dotBrush, x + padX, cy - 3, 6, 6);
            textX += dotW;
        }

        g.SmoothingMode = old;
        TextRenderer.DrawText(g, text, font, new Rectangle(textX, cy - ts.Height / 2, ts.Width, ts.Height), fg, TextFormatFlags.NoPadding);
        return w;
    }
}
