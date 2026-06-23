using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Globalization;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>Settings: Appearance (theme Segment + language), VNC configuration, and the local lock — each a
/// titled card with title+description rows. Cards stretch to the window width. See design_handoff.</summary>
public sealed class SettingsView : UserControl, IContentView
{
    private static readonly string[] Modes = { "light", "dark", "auto" };

    private readonly FlowLayoutPanel _root = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(22, 14, 22, 18) };
    private readonly LocalLockView _lock = new();
    private readonly Segment _theme = new(L.SettingsView_Light, L.SettingsView_Dark, L.SettingsView_AutoWindowsDefault);
    private readonly UiCombo _language = new(360);
    private readonly MaterialLabel _languageStatus = new() { AutoSize = false, Width = 640, Height = 22, Margin = new Padding(2, 2, 0, 8) };
    private readonly UiCombo _viewerScaleCombo = new(320);
    private readonly UiCombo _viewerColorCombo = new(320);
    private readonly UiCombo _vncPanelCombo = new(320);
    private readonly MaterialLabel _viewerScaleStatus = new() { AutoSize = false, Width = 640, Height = 22, Margin = new Padding(2, 2, 0, 8) };
    private readonly Action<string> _onTheme;
    private readonly Action<string, string> _onViewerPrefs;
    private readonly Action<string> _onVncPanel;
    private readonly AdminApi _api;
    private readonly bool _isAdmin;

    private sealed record LanguageItem(string Language, string Name) { public override string ToString() => Name; }
    private sealed record ScaleItem(string Value, string Name) { public override string ToString() => Name; }
    private sealed record ColorItem(string Value, string Name) { public override string ToString() => Name; }
    private sealed record PanelItem(string Value, string Name) { public override string ToString() => Name; }

    public SettingsView(string currentMode, Action<string> onTheme, bool isAdmin, AdminApi api, string viewerScale, string viewerColor, Action<string, string> onViewerPrefs, string vncPanel, Action<string> onVncPanel)
    {
        _onTheme = onTheme; _isAdmin = isAdmin; _api = api; _onViewerPrefs = onViewerPrefs; _onVncPanel = onVncPanel;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;

        // --- Appearance: theme Segment + language dropdown ---
        SelectMode(currentMode);
        _theme.SelectedChanged += (_, _) => _onTheme(Modes[_theme.SelectedIndex]);

        var languages = new List<object> { new LanguageItem(RuntimeLanguage.Auto, L.SettingsView_AutoSystemLanguage) };
        languages.AddRange(L.AvailableLanguages.Select(code => new LanguageItem(code, L.GetDisplayName(code))));
        _language.Items.AddRange(languages.ToArray());
        SelectLanguage(RuntimeLanguage.LoadPreference());
        _language.SelectedIndexChanged += (_, _) =>
        {
            if (_language.SelectedItem is not LanguageItem item) return;
            try { RuntimeLanguage.SavePreference(item.Language); RuntimeLanguage.Apply(item.Language); _languageStatus.Text = L.SettingsView_LanguageSavedRestartAffectedComponents; }
            catch (Exception ex) { _languageStatus.Text = L.SettingsView_CouldNotSaveLanguage + ex.Message; }
        };

        _root.Controls.Add(SettingsCard(L.SettingsView_Appearance,
            new SettingRow(L.SettingsView_Theme, L.SettingsView_ThemeDesc, _theme),
            new SettingRow(L.SettingsView_Language, L.SettingsView_LanguageDesc, _language)));
        _root.Controls.Add(_languageStatus);

        // --- VNC configuration: viewer scale, color depth, session panel ---
        _viewerScaleCombo.Items.AddRange(new object[]
        {
            new ScaleItem("auto", L.SettingsView_ViewerScaleAuto),
            new ScaleItem("50", "50%"), new ScaleItem("75", "75%"), new ScaleItem("100", "100%"),
            new ScaleItem("125", "125%"), new ScaleItem("150", "150%"), new ScaleItem("200", "200%"),
        });
        SelectScale(viewerScale);
        _viewerScaleCombo.SelectedIndexChanged += async (_, _) => await SaveViewerPrefsAsync();
        _viewerColorCombo.Items.AddRange(new object[] { new ColorItem("full", L.SettingsView_ViewerColorFull), new ColorItem("256", L.SettingsView_ViewerColor256) });
        SelectColor(viewerColor);
        _viewerColorCombo.SelectedIndexChanged += async (_, _) => await SaveViewerPrefsAsync();
        _vncPanelCombo.Items.AddRange(new object[]
        {
            new PanelItem("split", L.SettingsView_VncPanelSplit),
            new PanelItem("background", L.SettingsView_VncPanelBackground),
            new PanelItem("off", L.SettingsView_VncPanelOff),
        });
        SelectPanel(vncPanel);
        _vncPanelCombo.SelectedIndexChanged += (_, _) => { if (_vncPanelCombo.SelectedItem is PanelItem p) _onVncPanel(p.Value); };

        _root.Controls.Add(SettingsCard(L.SettingsView_VncConfig,
            new SettingRow(L.SettingsView_ViewerScale, L.SettingsView_ViewerScaleDesc, _viewerScaleCombo),
            new SettingRow(L.SettingsView_ViewerColor, L.SettingsView_ViewerColorDesc, _viewerColorCombo),
            new SettingRow(L.SettingsView_VncPanel, L.SettingsView_VncPanelDesc, _vncPanelCombo)));
        _root.Controls.Add(_viewerScaleStatus);

        // --- Local lock (admin only) ---
        if (_isAdmin)
        {
            _lock.Dock = DockStyle.Fill;
            _root.Controls.Add(new Card(null, null, _lock) { Width = 980, Height = 242, Margin = new Padding(0, 6, 0, 0) });
        }

        _root.ClientSizeChanged += (_, _) => FitWidth();
        Controls.Add(_root);
        FitWidth();
    }

    /// <summary>A titled settings card whose rows stretch to the card width (so the right-hand controls
    /// sit at the right edge instead of clipping).</summary>
    private static Card SettingsCard(string title, params SettingRow[] rows)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows.Length, BackColor = ThemeManager.Panel };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var r in rows) { grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 56)); r.Dock = DockStyle.Fill; grid.Controls.Add(r); }
        return new Card(title, null, grid) { Width = 980, Height = 46 + rows.Length * 56 + 10, Margin = new Padding(0, 0, 0, 6) };
    }

    private void FitWidth()
    {
        int w = _root.ClientSize.Width - _root.Padding.Horizontal - 2;
        if (w <= 0) return;
        foreach (Control c in _root.Controls) if (c is Card) c.Width = w;
    }

    private void SelectMode(string mode)
    {
        int i = Array.FindIndex(Modes, m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase));
        _theme.SelectedIndex = i >= 0 ? i : 1; // default dark
    }

    private void SelectLanguage(string language)
    {
        var normalized = RuntimeLanguage.Normalize(language);
        for (int i = 0; i < _language.Items.Count; i++)
            if (_language.Items[i] is LanguageItem item && item.Language == normalized) { _language.SelectedIndex = i; return; }
        _language.SelectedIndex = 0;
    }

    private void SelectScale(string value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant();
        for (int i = 0; i < _viewerScaleCombo.Items.Count; i++)
            if (_viewerScaleCombo.Items[i] is ScaleItem s && string.Equals(s.Value, v, StringComparison.OrdinalIgnoreCase)) { _viewerScaleCombo.SelectedIndex = i; return; }
        _viewerScaleCombo.SelectedIndex = 0;
    }

    private void SelectColor(string value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? "full" : value.Trim().ToLowerInvariant();
        for (int i = 0; i < _viewerColorCombo.Items.Count; i++)
            if (_viewerColorCombo.Items[i] is ColorItem c && string.Equals(c.Value, v, StringComparison.OrdinalIgnoreCase)) { _viewerColorCombo.SelectedIndex = i; return; }
        _viewerColorCombo.SelectedIndex = 0;
    }

    private void SelectPanel(string value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? "split" : value.Trim().ToLowerInvariant();
        for (int i = 0; i < _vncPanelCombo.Items.Count; i++)
            if (_vncPanelCombo.Items[i] is PanelItem p && string.Equals(p.Value, v, StringComparison.OrdinalIgnoreCase)) { _vncPanelCombo.SelectedIndex = i; return; }
        _vncPanelCombo.SelectedIndex = 0;
    }

    private async Task SaveViewerPrefsAsync()
    {
        if (_viewerScaleCombo.SelectedItem is not ScaleItem scale || _viewerColorCombo.SelectedItem is not ColorItem color) return;
        try
        {
            await _api.UpdateViewerPrefsAsync(scale.Value, color.Value);
            _onViewerPrefs(scale.Value, color.Value);
            _viewerScaleStatus.Text = L.SettingsView_ViewerScaleSaved;
        }
        catch (Exception ex) { _viewerScaleStatus.Text = L.SettingsView_CouldNotSaveScale + ex.Message; }
    }

    public async Task OnShownAsync() { FitWidth(); if (_isAdmin) await _lock.OnShownAsync(); }
    public void ApplyTheme() { BackColor = ThemeManager.Bg; if (_isAdmin) _lock.ApplyTheme(); Invalidate(true); }
}
