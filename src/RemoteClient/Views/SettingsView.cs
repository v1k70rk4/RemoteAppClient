using System.Drawing;
using MaterialSkin;
using MaterialSkin.Controls;
using RemoteAgent.Globalization;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>Settings: Appearance (Light / Dark / Auto theme), language, and local lock.</summary>
public sealed class SettingsView : UserControl, IContentView
{
    private readonly LocalLockView _lock = new();
    private readonly MaterialComboBox _theme = new() { Hint = L.SettingsView_Theme, Width = 260 };
    private readonly MaterialComboBox _language = new() { Hint = L.SettingsView_Language, Width = 260 };
    private readonly MaterialLabel _languageStatus = new() { AutoSize = false, Width = 560, Height = 44, Margin = new Padding(0, 6, 0, 0) };
    private readonly MaterialComboBox _viewerScaleCombo = new() { Hint = L.SettingsView_ViewerScale, Width = 260 };
    private readonly MaterialComboBox _viewerColorCombo = new() { Hint = L.SettingsView_ViewerColor, Width = 260 };
    private readonly MaterialComboBox _vncPanelCombo = new() { Hint = L.SettingsView_VncPanel, Width = 260 };
    private readonly MaterialLabel _viewerScaleStatus = new() { AutoSize = false, Width = 560, Height = 28, Margin = new Padding(0, 6, 0, 0) };
    private readonly Action<string> _onTheme;
    private readonly Action<string, string> _onViewerPrefs;
    private readonly Action<string> _onVncPanel;
    private readonly AdminApi _api;
    private readonly bool _isAdmin;

    private sealed record ThemeItem(string Mode, string Name) { public override string ToString() => Name; }
    private sealed record LanguageItem(string Language, string Name) { public override string ToString() => Name; }
    private sealed record ScaleItem(string Value, string Name) { public override string ToString() => Name; }
    private sealed record ColorItem(string Value, string Name) { public override string ToString() => Name; }
    private sealed record PanelItem(string Value, string Name) { public override string ToString() => Name; }

    public SettingsView(string currentMode, Action<string> onTheme, bool isAdmin, AdminApi api, string viewerScale, string viewerColor, Action<string, string> onViewerPrefs, string vncPanel, Action<string> onVncPanel)
    {
        _onTheme = onTheme;
        _isAdmin = isAdmin;
        _api = api;
        _onViewerPrefs = onViewerPrefs;
        _onVncPanel = onVncPanel;
        Dock = DockStyle.Fill;

        // Scrollable root so the page never clips; two dropdowns per row (combo Hint = field label).
        var root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(24, 14, 24, 16),
        };

        // --- Appearance: theme + language side by side ---
        root.Controls.Add(Header(L.SettingsView_Appearance));
        _theme.Items.AddRange(new object[] { new ThemeItem("light", L.SettingsView_Light), new ThemeItem("dark", L.SettingsView_Dark), new ThemeItem("auto", L.SettingsView_AutoWindowsDefault) });
        SelectMode(currentMode);
        _theme.SelectedIndexChanged += (_, _) => { if (_theme.SelectedItem is ThemeItem t) _onTheme(t.Mode); };

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
        root.Controls.Add(Pair(_theme, _language));
        root.Controls.Add(_languageStatus);

        // --- VNC configuration: viewer scale + color depth side by side ---
        root.Controls.Add(Header(L.SettingsView_VncConfig));
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
        root.Controls.Add(Pair(_viewerScaleCombo, _viewerColorCombo));

        // Session panel layout on connect (local, per machine).
        _vncPanelCombo.Items.AddRange(new object[]
        {
            new PanelItem("split", L.SettingsView_VncPanelSplit),
            new PanelItem("background", L.SettingsView_VncPanelBackground),
            new PanelItem("off", L.SettingsView_VncPanelOff),
        });
        SelectPanel(vncPanel);
        _vncPanelCombo.Margin = new Padding(0, 6, 0, 2);
        _vncPanelCombo.SelectedIndexChanged += (_, _) => { if (_vncPanelCombo.SelectedItem is PanelItem p) _onVncPanel(p.Value); };
        root.Controls.Add(_vncPanelCombo);
        root.Controls.Add(_viewerScaleStatus);

        // --- Local lock (admin only), below ---
        if (_isAdmin)
        {
            root.Controls.Add(new MaterialDivider { Width = 460, Margin = new Padding(0, 16, 0, 8) });
            _lock.Dock = DockStyle.None;
            // Auto-fit the height (no empty area / scrollbar) but keep a fixed width: the inner labels
            // are top-docked, so without a width floor AutoSize collapses the control and clips them.
            _lock.AutoSize = true;
            _lock.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _lock.MinimumSize = new Size(738, 0);
            root.Controls.Add(_lock);
        }

        Controls.Add(root);
    }

    private static MaterialLabel Header(string text) =>
        new() { Text = text, Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 14, 0, 6) };

    // Two dropdowns side by side; each combo's Hint is its field label.
    private static Control Pair(Control left, Control right)
    {
        left.Margin = new Padding(0, 2, 32, 2);
        right.Margin = new Padding(0, 2, 0, 2);
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        row.Controls.Add(left);
        row.Controls.Add(right);
        return row;
    }

    private void SelectMode(string mode)
    {
        for (int i = 0; i < _theme.Items.Count; i++)
            if (_theme.Items[i] is ThemeItem t && string.Equals(t.Mode, mode, StringComparison.OrdinalIgnoreCase)) { _theme.SelectedIndex = i; return; }
        _theme.SelectedIndex = 1; // dark
    }

    private void SelectLanguage(string language)
    {
        var normalized = RuntimeLanguage.Normalize(language);
        for (int i = 0; i < _language.Items.Count; i++)
            if (_language.Items[i] is LanguageItem item && item.Language == normalized) { _language.SelectedIndex = i; return; }
        _language.SelectedIndex = 0; // auto
    }

    private void SelectScale(string value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant();
        for (int i = 0; i < _viewerScaleCombo.Items.Count; i++)
            if (_viewerScaleCombo.Items[i] is ScaleItem s && string.Equals(s.Value, v, StringComparison.OrdinalIgnoreCase)) { _viewerScaleCombo.SelectedIndex = i; return; }
        _viewerScaleCombo.SelectedIndex = 0; // auto
    }

    private void SelectColor(string value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? "full" : value.Trim().ToLowerInvariant();
        for (int i = 0; i < _viewerColorCombo.Items.Count; i++)
            if (_viewerColorCombo.Items[i] is ColorItem c && string.Equals(c.Value, v, StringComparison.OrdinalIgnoreCase)) { _viewerColorCombo.SelectedIndex = i; return; }
        _viewerColorCombo.SelectedIndex = 0; // full
    }

    private void SelectPanel(string value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? "split" : value.Trim().ToLowerInvariant();
        for (int i = 0; i < _vncPanelCombo.Items.Count; i++)
            if (_vncPanelCombo.Items[i] is PanelItem p && string.Equals(p.Value, v, StringComparison.OrdinalIgnoreCase)) { _vncPanelCombo.SelectedIndex = i; return; }
        _vncPanelCombo.SelectedIndex = 0; // split
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
        catch (Exception ex)
        {
            _viewerScaleStatus.Text = L.SettingsView_CouldNotSaveScale + ex.Message;
        }
    }

    public async Task OnShownAsync() { if (_isAdmin) await _lock.OnShownAsync(); }
    public void ApplyTheme() { ThemeManager.StyleView(this); if (_isAdmin) _lock.ApplyTheme(); }
}
