using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient;

/// <summary>Egy felhasználó Windows Hello eszközei (admin): listázás + visszavonás.</summary>
public sealed class HelloDevicesForm : MaterialForm
{
    private readonly AdminApi _api;
    private readonly Guid _userId;
    private readonly ListView _list = new();
    private readonly MaterialLabel _status = new();

    public HelloDevicesForm(AdminApi api, Guid userId, string username)
    {
        _api = api; _userId = userId;
        ThemeManager.Skin.AddFormToManage(this);
        Text = $"Windows Hello eszközök — {username}";
        Sizable = false;
        Width = 560; Height = 420;
        StartPosition = FormStartPosition.CenterParent;

        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false; _list.Dock = DockStyle.Fill; _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add("Eszköz", 220);
        _list.Columns.Add("Létrehozva", 150);
        _list.Columns.Add("Utoljára használt", 150);
        ThemeManager.StyleList(_list);

        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(12, 10, 12, 6) };
        var refreshBtn = new MaterialButton { Text = "Frissítés", AutoSize = true, Margin = new Padding(4, 0, 4, 0) };
        refreshBtn.Click += async (_, _) => await RefreshAsync();
        var revokeBtn = new MaterialButton { Text = "Visszavonás", AutoSize = true, Margin = new Padding(4, 0, 4, 0), Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        revokeBtn.Click += async (_, _) => await RevokeAsync();
        tools.Controls.AddRange([refreshBtn, revokeBtn]);

        var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 30 };
        _status.AutoSize = false; _status.Dock = DockStyle.Fill; _status.AutoEllipsis = true;
        _status.TextAlign = ContentAlignment.MiddleLeft; _status.Padding = new Padding(14, 0, 14, 0);
        statusPanel.Controls.Add(_status);

        Controls.Add(_list);
        Controls.Add(tools);
        Controls.Add(statusPanel);

        Load += async (_, _) => await RefreshAsync();
    }

    private HelloCredentialInfo? Selected() => _list.SelectedItems.Count == 0 ? null : (HelloCredentialInfo)_list.SelectedItems[0].Tag!;

    private async Task RefreshAsync()
    {
        try
        {
            var creds = await _api.GetUserHelloAsync(_userId);
            _list.Items.Clear();
            foreach (var c in creds)
            {
                var it = new ListViewItem(c.DeviceName) { Tag = c };
                it.SubItems.Add(c.CreatedAt.LocalDateTime.ToString("g"));
                it.SubItems.Add(c.LastUsedAt?.LocalDateTime.ToString("g") ?? "—");
                _list.Items.Add(it);
            }
            _status.Text = creds.Count == 0 ? "Nincs Windows Hello eszköz ehhez a felhasználóhoz." : $"{creds.Count} Hello eszköz.";
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }

    private async Task RevokeAsync()
    {
        if (Selected() is not { } c) return;
        if (MessageBox.Show($"Visszavonod ezt a Windows Hello eszközt?\n\n{c.DeviceName}\n\nUtána erről a gépről nem lehet vele belépni (a jelszó+TOTP marad).",
                "Hello visszavonása", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.RevokeUserHelloAsync(_userId, c.Id); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }
}
