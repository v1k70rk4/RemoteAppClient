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
    private readonly MaterialComboBox _theme = new() { Hint = L.SettingsView_001, Width = 260 };
    private readonly MaterialComboBox _language = new() { Hint = L.SettingsView_006, Width = 260 };
    private readonly MaterialLabel _languageStatus = new() { AutoSize = false, Width = 560, Height = 44, Margin = new Padding(0, 6, 0, 0) };
    private readonly Action<string> _onTheme;
    private readonly bool _isAdmin;

    private sealed record ThemeItem(string Mode, string Name) { public override string ToString() => Name; }
    private sealed record LanguageItem(string Language, string Name) { public override string ToString() => Name; }

    public SettingsView(string currentMode, Action<string> onTheme, bool isAdmin)
    {
        _onTheme = onTheme;
        _isAdmin = isAdmin;
        Dock = DockStyle.Fill;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(24, 18, 24, 8) };
        top.Controls.Add(new MaterialLabel { Text = L.SettingsView_002, Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        top.Controls.Add(new MaterialLabel { Text = L.SettingsView_001, FontType = MaterialSkinManager.fontType.Caption, AutoSize = true, Margin = new Padding(0, 4, 0, 2) });
        _theme.Items.AddRange(new object[] { new ThemeItem("light", L.SettingsView_003), new ThemeItem("dark", L.SettingsView_004), new ThemeItem("auto", L.SettingsView_005) });
        SelectMode(currentMode);
        _theme.SelectedIndexChanged += (_, _) => { if (_theme.SelectedItem is ThemeItem t) _onTheme(t.Mode); };
        top.Controls.Add(_theme);
        top.Controls.Add(new MaterialLabel { Text = L.SettingsView_006, FontType = MaterialSkinManager.fontType.Caption, AutoSize = true, Margin = new Padding(0, 14, 0, 2) });
        var languages = new List<object> { new LanguageItem(RuntimeLanguage.Auto, L.SettingsView_007) };
        languages.AddRange(L.AvailableLanguages.Select(code => new LanguageItem(code, L.GetDisplayName(code))));
        _language.Items.AddRange(languages.ToArray());
        SelectLanguage(RuntimeLanguage.LoadPreference());
        _language.SelectedIndexChanged += (_, _) =>
        {
            if (_language.SelectedItem is not LanguageItem item) return;
            try
            {
                RuntimeLanguage.SavePreference(item.Language);
                RuntimeLanguage.Apply(item.Language);
                _languageStatus.Text = L.SettingsView_010;
            }
            catch (Exception ex)
            {
                _languageStatus.Text = L.SettingsView_011 + ex.Message;
            }
        };
        top.Controls.Add(_language);
        top.Controls.Add(_languageStatus);
        top.Controls.Add(new MaterialDivider { Width = 460, Margin = new Padding(0, 16, 0, 0) });

        if (_isAdmin) Controls.Add(_lock); // Fill; local lock is admin-only
        Controls.Add(top);                 // Top; appearance/language
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

    public async Task OnShownAsync() { if (_isAdmin) await _lock.OnShownAsync(); }
    public void ApplyTheme() { ThemeManager.StyleView(this); if (_isAdmin) _lock.ApplyTheme(); }
}
