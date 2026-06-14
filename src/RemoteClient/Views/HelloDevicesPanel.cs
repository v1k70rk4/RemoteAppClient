using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>Egy felhasználó Windows Hello eszközei (listázás + visszavonás) — beágyazható panel a „Windows Hello" füléhez.</summary>
public sealed class HelloDevicesPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly Guid _userId;
    private readonly ListView _list = new();
    private readonly MaterialLabel _status = new();

    public HelloDevicesPanel(AdminApi api, Guid userId)
    {
        _api = api; _userId = userId;
        Dock = DockStyle.Fill;

        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false; _list.Dock = DockStyle.Fill; _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add(L.HelloDevicesPanel_001, 220);
        _list.Columns.Add(L.BootstrapView_008, 150);
        _list.Columns.Add(L.HelloDevicesPanel_002, 150);

        var tools = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(8, 6, 8, 6) };
        var refreshBtn = new MaterialButton { Text = L.AboutView_002, AutoSize = true, Margin = new Padding(4, 0, 4, 0) };
        refreshBtn.Click += async (_, _) => await ShownAsync();
        var revokeBtn = new MaterialButton { Text = L.BootstrapView_012, AutoSize = true, Margin = new Padding(4, 0, 4, 0), Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        revokeBtn.Click += async (_, _) => await RevokeAsync();
        tools.Controls.AddRange([refreshBtn, revokeBtn]);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 30 };
        _status.AutoSize = false; _status.Dock = DockStyle.Fill; _status.AutoEllipsis = true;
        _status.TextAlign = ContentAlignment.MiddleLeft; _status.Padding = new Padding(12, 0, 12, 0);
        bottom.Controls.Add(_status);

        Controls.Add(_list);
        Controls.Add(tools);
        Controls.Add(bottom);
    }

    private HelloCredentialInfo? Selected() => _list.SelectedItems.Count == 0 ? null : (HelloCredentialInfo)_list.SelectedItems[0].Tag!;

    public async Task ShownAsync()
    {
        ThemeManager.StyleList(_list);
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
            _status.Text = creds.Count == 0 ? L.HelloDevicesPanel_003 : L.Format(L.HelloDevicesPanel_004, creds.Count);
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_019 + ex.Message; }
    }

    private async Task RevokeAsync()
    {
        if (Selected() is not { } c) return;
        if (MessageBox.Show(L.Format(L.HelloDevicesPanel_005, c.DeviceName),
                L.HelloDevicesPanel_006, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.RevokeUserHelloAsync(_userId, c.Id); await ShownAsync(); }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_019 + ex.Message; }
    }
}
