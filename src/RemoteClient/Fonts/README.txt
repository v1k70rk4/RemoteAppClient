IBM Plex design fonts (bundled)
================================

Drop the IBM Plex .ttf files into THIS folder. They are embedded into the exe at build time
(csproj: <EmbeddedResource Include="Fonts\*.ttf" />) and loaded at startup by UiFont.cs, so the
design font ships inside the single-file, self-updating exe with no per-machine install.

Download (free, SIL Open Font License — redistributable):
  https://github.com/IBM/plex/releases     (zip with the static .ttf files)
  or Google Fonts: "IBM Plex Sans" + "IBM Plex Mono"

Use the STATIC .ttf files (NOT the variable-font "IBMPlexSansVar" / "...Var" ones).

Required:
  IBMPlexSans-Regular.ttf      -> family "IBM Plex Sans"        (Regular + Bold cover body/title)
  IBMPlexSans-Bold.ttf         -> family "IBM Plex Sans"        (Bold style: PageTitle, auth Title)
  IBMPlexSans-SemiBold.ttf     -> family "IBM Plex Sans SemiBold" (captions, nav-on, section titles)
  IBMPlexMono-Regular.ttf      -> family "IBM Plex Mono"        (hostnames, IPs, IDs, timestamps)
  IBMPlexMono-Bold.ttf         -> family "IBM Plex Mono"        (Bold: stat numbers, host title)

Optional:
  IBMPlexSans-Medium.ttf       -> family "IBM Plex Sans Medium"

How it works:
  - Any *.ttf in this folder is embedded automatically (no per-file csproj edits).
  - UiFont registers each with GDI (AddFontMemResourceEx) AND GDI+ (PrivateFontCollection),
    so the TextRenderer-based owner-drawn UI renders them by family name.
  - If this folder has no .ttf, the app falls back to Segoe UI / Segoe UI Semibold / Consolas.
