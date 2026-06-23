using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace RemoteClient;

/// <summary>
/// Type scale for the redesigned console (design_handoff_console_redesign). Sizes are in pixels
/// (GraphicsUnit.Pixel) to match the spec directly. Uses the bundled IBM Plex Sans / IBM Plex Mono
/// (embedded .ttf, see Fonts/), otherwise falls back to Segoe UI / Segoe UI Semibold / Consolas.
/// Fonts are created once and reused.
/// </summary>
public static class UiFont
{
    // IBM Plex (OFL) is embedded as resources so the design font ships inside the single-file, self-updating
    // exe — no per-machine install. Each .ttf is registered with BOTH GDI (AddFontMemResourceEx, so the
    // TextRenderer/GDI path the owner-drawn UI uses finds it by name) and GDI+ (PrivateFontCollection, whose
    // FontFamily objects we render with so Font.Name stays correct). Declared before the family fields so it
    // populates first. With no embedded .ttf the maps stay empty and we fall back to system fonts.
    private static readonly PrivateFontCollection _pfc = new();
    private static readonly Dictionary<string, FontFamily> _bundled = LoadBundled();

    private static readonly string Sans = Pick("IBM Plex Sans", "Segoe UI");
    private static readonly string SansSemi = Pick("IBM Plex Sans SemiBold", "IBM Plex Sans Medium", "Segoe UI Semibold", "Segoe UI");
    private static readonly string MonoFamily = Pick("IBM Plex Mono", "Consolas", "Courier New");

    [DllImport("gdi32.dll")]
    private static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, out uint pcFonts);

    private static Dictionary<string, FontFamily> LoadBundled()
    {
        var map = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var asm = typeof(UiFont).Assembly;
            foreach (var res in asm.GetManifestResourceNames())
            {
                if (!res.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)) continue;
                using var s = asm.GetManifestResourceStream(res);
                if (s is null) continue;
                var bytes = new byte[s.Length];
                s.ReadExactly(bytes);
                // Kept allocated for the process lifetime: the GDI+ collection references this memory.
                var ptr = Marshal.AllocCoTaskMem(bytes.Length);
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                AddFontMemResourceEx(ptr, (uint)bytes.Length, IntPtr.Zero, out _);  // GDI  (TextRenderer)
                _pfc.AddMemoryFont(ptr, bytes.Length);                               // GDI+ (FontFamily)
            }
            foreach (var fam in _pfc.Families) map[fam.Name] = fam;
        }
        catch { /* fall back to installed/system fonts */ }
        return map;
    }

    private static string Pick(params string[] candidates)
    {
        foreach (var name in candidates)
            if (_bundled.ContainsKey(name)) return name;   // bundled IBM Plex wins
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var ic = new InstalledFontCollection())
            foreach (var fam in ic.Families)
                installed.Add(fam.Name);
        foreach (var name in candidates)
            if (installed.Contains(name)) return name;
        return candidates[^1];
    }

    private static Font Px(string family, float px, FontStyle style = FontStyle.Regular)
    {
        if (_bundled.TryGetValue(family, out var fam))
        {
            if (!fam.IsStyleAvailable(style)) style = FontStyle.Regular;  // a bundled weight may ship as its own family
            if (fam.IsStyleAvailable(style)) return new Font(fam, px, style, GraphicsUnit.Pixel);
        }
        return new Font(family, px, style, GraphicsUnit.Pixel);
    }

    // Scale per the handoff (page title 17/700, section 13/600, body 12.5-13, label 11/600,
    // stat number 27/700 mono). 600 maps to the Semibold family; 700 to Bold.
    public static readonly Font PageTitle    = Px(Sans, 17, FontStyle.Bold);
    public static readonly Font Title        = Px(Sans, 22, FontStyle.Bold);   // auth / large section heading ("Sign in")
    public static readonly Font SectionTitle = Px(SansSemi, 13);
    public static readonly Font Body         = Px(Sans, 13);
    public static readonly Font BodySemi     = Px(SansSemi, 13);
    public static readonly Font Small        = Px(Sans, 11.5f);
    public static readonly Font Label        = Px(SansSemi, 11);   // caller uppercases; tracking is drawn manually
    public static readonly Font NavLabel     = Px(Sans, 13);
    public static readonly Font NavLabelOn   = Px(SansSemi, 13);
    public static readonly Font Mono         = Px(MonoFamily, 12.5f);
    public static readonly Font MonoSemi     = Px(MonoFamily, 13, FontStyle.Bold);
    public static readonly Font MonoSmall    = Px(MonoFamily, 11);
    public static readonly Font StatNumber   = Px(MonoFamily, 27, FontStyle.Bold);
    public static readonly Font HostTitle    = Px(MonoFamily, 19, FontStyle.Bold);
}
