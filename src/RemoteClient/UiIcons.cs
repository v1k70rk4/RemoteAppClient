using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace RemoteClient;

/// <summary>
/// Minimal stroked line-icon set for the redesigned console nav / topbar. Each glyph is drawn inside the
/// given (square-ish) box, centered, in <paramref name="color"/>. Keys: monitor, users, layers, box,
/// terminal, list, server, gear, info, sun, moon. Unknown keys fall back to a small ring.
/// Intentionally simple 18px UI glyphs (none illustrative) — see design_handoff_console_redesign.
/// </summary>
public static class UiIcons
{
    public static void Draw(Graphics g, string key, RectangleF box, Color color, float stroke = 1.6f)
    {
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        float s = Math.Min(box.Width, box.Height);
        float x = box.X + (box.Width - s) / 2f;
        float y = box.Y + (box.Height - s) / 2f;

        // Normalized (0..1) drawing helpers within the glyph box.
        void Ln(float a, float b, float c, float d) => g.DrawLine(pen, x + a * s, y + b * s, x + c * s, y + d * s);
        void Rc(float a, float b, float w, float h) => g.DrawRectangle(pen, x + a * s, y + b * s, w * s, h * s);
        void El(float a, float b, float w, float h) => g.DrawEllipse(pen, x + a * s, y + b * s, w * s, h * s);
        void Ar(float a, float b, float w, float h, float st, float sw) => g.DrawArc(pen, x + a * s, y + b * s, w * s, h * s, st, sw);
        void Dot(float a, float b) => g.FillEllipse(pen.Brush, x + a * s - 1f, y + b * s - 1f, 2.2f, 2.2f);

        switch (key)
        {
            case "monitor":
                Rc(0.12f, 0.18f, 0.76f, 0.46f); Ln(0.5f, 0.64f, 0.5f, 0.80f); Ln(0.34f, 0.82f, 0.66f, 0.82f);
                break;
            case "users":
                El(0.30f, 0.12f, 0.24f, 0.24f); Ar(0.16f, 0.42f, 0.52f, 0.50f, 180, 180);
                break;
            case "layers":
                Ln(0.5f, 0.12f, 0.86f, 0.32f); Ln(0.86f, 0.32f, 0.5f, 0.52f); Ln(0.5f, 0.52f, 0.14f, 0.32f); Ln(0.14f, 0.32f, 0.5f, 0.12f);
                Ln(0.14f, 0.48f, 0.5f, 0.68f); Ln(0.5f, 0.68f, 0.86f, 0.48f);
                break;
            case "box":
                Rc(0.16f, 0.20f, 0.68f, 0.62f); Ln(0.16f, 0.40f, 0.84f, 0.40f); Ln(0.5f, 0.20f, 0.5f, 0.40f);
                break;
            case "terminal":
                Rc(0.12f, 0.18f, 0.76f, 0.64f); Ln(0.26f, 0.40f, 0.40f, 0.52f); Ln(0.40f, 0.52f, 0.26f, 0.64f); Ln(0.50f, 0.66f, 0.66f, 0.66f);
                break;
            case "list":
                Ln(0.32f, 0.26f, 0.84f, 0.26f); Ln(0.32f, 0.50f, 0.84f, 0.50f); Ln(0.32f, 0.74f, 0.84f, 0.74f);
                Dot(0.18f, 0.26f); Dot(0.18f, 0.50f); Dot(0.18f, 0.74f);
                break;
            case "server":
                Rc(0.14f, 0.18f, 0.72f, 0.26f); Rc(0.14f, 0.54f, 0.72f, 0.26f); Dot(0.24f, 0.31f); Dot(0.24f, 0.67f);
                break;
            case "gear":
                El(0.28f, 0.28f, 0.44f, 0.44f); El(0.42f, 0.42f, 0.16f, 0.16f);
                Ln(0.5f, 0.10f, 0.5f, 0.24f); Ln(0.5f, 0.76f, 0.5f, 0.90f); Ln(0.10f, 0.5f, 0.24f, 0.5f); Ln(0.76f, 0.5f, 0.90f, 0.5f);
                break;
            case "info":
                El(0.16f, 0.16f, 0.68f, 0.68f); Dot(0.5f, 0.34f); Ln(0.5f, 0.46f, 0.5f, 0.70f);
                break;
            case "sun":
                El(0.34f, 0.34f, 0.32f, 0.32f);
                Ln(0.5f, 0.08f, 0.5f, 0.20f); Ln(0.5f, 0.80f, 0.5f, 0.92f); Ln(0.08f, 0.5f, 0.20f, 0.5f); Ln(0.80f, 0.5f, 0.92f, 0.5f);
                Ln(0.20f, 0.20f, 0.29f, 0.29f); Ln(0.71f, 0.71f, 0.80f, 0.80f); Ln(0.20f, 0.80f, 0.29f, 0.71f); Ln(0.71f, 0.29f, 0.80f, 0.20f);
                break;
            case "moon":
            {
                // Filled crescent = a disc minus an offset disc (so it reads as a moon, not a "C").
                using var disc = new GraphicsPath(); disc.AddEllipse(x + 0.16f * s, y + 0.16f * s, 0.62f * s, 0.62f * s);
                using var bite = new GraphicsPath(); bite.AddEllipse(x + 0.34f * s, y + 0.08f * s, 0.62f * s, 0.62f * s);
                using var reg = new Region(disc); reg.Exclude(bite);
                g.FillRegion(pen.Brush, reg);
                break;
            }
            case "chevron":   // left chevron (back)
                Ln(0.60f, 0.24f, 0.36f, 0.50f); Ln(0.36f, 0.50f, 0.60f, 0.76f);
                break;
            case "chevrondown":   // down chevron (dropdown)
                Ln(0.28f, 0.42f, 0.50f, 0.62f); Ln(0.50f, 0.62f, 0.72f, 0.42f);
                break;
            case "dots":      // vertical ellipsis (overflow menu)
                Dot(0.5f, 0.26f); Dot(0.5f, 0.5f); Dot(0.5f, 0.74f);
                break;
            case "folder":
                Rc(0.14f, 0.36f, 0.72f, 0.40f);   // body
                Ln(0.16f, 0.36f, 0.34f, 0.36f); Ln(0.34f, 0.36f, 0.40f, 0.28f); Ln(0.40f, 0.28f, 0.56f, 0.28f); Ln(0.56f, 0.28f, 0.58f, 0.36f);  // tab
                break;
            case "play":
                g.FillPolygon(pen.Brush, new[] { new PointF(x + 0.34f * s, y + 0.26f * s), new PointF(x + 0.34f * s, y + 0.74f * s), new PointF(x + 0.74f * s, y + 0.50f * s) });
                break;
            case "search":
                El(0.20f, 0.20f, 0.42f, 0.42f); Ln(0.63f, 0.63f, 0.82f, 0.82f);
                break;
            case "person":
                El(0.34f, 0.14f, 0.32f, 0.32f);            // head
                Ar(0.18f, 0.54f, 0.64f, 0.56f, 180, 180);  // shoulders
                break;
            case "plus":
                Ln(0.5f, 0.22f, 0.5f, 0.78f); Ln(0.22f, 0.5f, 0.78f, 0.5f);
                break;
            case "lock":
                Rc(0.30f, 0.46f, 0.40f, 0.38f);              // body
                Ar(0.34f, 0.20f, 0.32f, 0.42f, 180, 180);    // shackle
                Dot(0.5f, 0.63f);                            // keyhole
                break;
            case "chat":      // speech bubble with two text lines (ask availability)
                Rc(0.14f, 0.18f, 0.72f, 0.48f);              // bubble
                Ln(0.30f, 0.66f, 0.30f, 0.82f); Ln(0.30f, 0.82f, 0.46f, 0.66f);  // tail
                Ln(0.28f, 0.34f, 0.72f, 0.34f); Ln(0.28f, 0.50f, 0.58f, 0.50f);  // text lines
                break;
            case "refresh":
            {
                // Reload: a ring open at the top with a filled arrowhead at the right end, sweeping clockwise.
                Ar(0.18f, 0.18f, 0.64f, 0.64f, 305f, 290f);
                double a = 305.0 * Math.PI / 180.0;                         // arrowhead sits at the arc's start
                float hx = x + (0.5f + 0.32f * (float)Math.Cos(a)) * s, hy = y + (0.5f + 0.32f * (float)Math.Sin(a)) * s;
                float tgx = (float)Math.Cos(a + Math.PI / 2), tgy = (float)Math.Sin(a + Math.PI / 2);  // clockwise tangent
                float rdx = (float)Math.Cos(a), rdy = (float)Math.Sin(a);                              // outward radial
                g.FillPolygon(pen.Brush, new[]
                {
                    new PointF(hx + tgx * 0.14f * s, hy + tgy * 0.14f * s),
                    new PointF(hx + rdx * 0.09f * s, hy + rdy * 0.09f * s),
                    new PointF(hx - rdx * 0.09f * s, hy - rdy * 0.09f * s),
                });
                break;
            }
            default:
                El(0.28f, 0.28f, 0.44f, 0.44f);
                break;
        }
        g.SmoothingMode = oldMode;
    }
}
