using System.Drawing;
using MaterialSkin;
using MaterialSkin.Controls;

namespace RemoteClient.Views;

/// <summary>Beállítások: Megjelenés (téma: Világos / Sötét / Automata) + Helyi zár.</summary>
public sealed class SettingsView : UserControl, IContentView
{
    private readonly LocalLockView _lock = new();
    private readonly MaterialComboBox _theme = new() { Hint = "Téma", Width = 260 };
    private readonly Action<string> _onTheme;
    private readonly bool _isAdmin;

    private sealed record ThemeItem(string Mode, string Name) { public override string ToString() => Name; }

    public SettingsView(string currentMode, Action<string> onTheme, bool isAdmin)
    {
        _onTheme = onTheme;
        _isAdmin = isAdmin;
        Dock = DockStyle.Fill;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(24, 18, 24, 8) };
        top.Controls.Add(new MaterialLabel { Text = "Megjelenés", Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        top.Controls.Add(new MaterialLabel { Text = "Téma", FontType = MaterialSkinManager.fontType.Caption, AutoSize = true, Margin = new Padding(0, 4, 0, 2) });
        _theme.Items.AddRange(new object[] { new ThemeItem("light", "Világos"), new ThemeItem("dark", "Sötét"), new ThemeItem("auto", "Automata (Windows szerint)") });
        SelectMode(currentMode);
        _theme.SelectedIndexChanged += (_, _) => { if (_theme.SelectedItem is ThemeItem t) _onTheme(t.Mode); };
        top.Controls.Add(_theme);
        top.Controls.Add(new MaterialDivider { Width = 460, Margin = new Padding(0, 16, 0, 0) });

        if (_isAdmin) Controls.Add(_lock); // Fill — a Helyi zár CSAK adminnak
        Controls.Add(top);                 // Top — Megjelenés
    }

    private void SelectMode(string mode)
    {
        for (int i = 0; i < _theme.Items.Count; i++)
            if (_theme.Items[i] is ThemeItem t && string.Equals(t.Mode, mode, StringComparison.OrdinalIgnoreCase)) { _theme.SelectedIndex = i; return; }
        _theme.SelectedIndex = 1; // dark
    }

    public async Task OnShownAsync() { if (_isAdmin) await _lock.OnShownAsync(); }
    public void ApplyTheme() { ThemeManager.StyleView(this); if (_isAdmin) _lock.ApplyTheme(); }
}
