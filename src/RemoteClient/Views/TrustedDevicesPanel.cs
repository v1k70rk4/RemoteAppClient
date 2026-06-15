using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>User trusted ("remember this device") machines: list with expiry + revoke, embedded as a tab.</summary>
public sealed class TrustedDevicesPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly Guid _userId;
    private readonly ListView _list = new();
    private readonly MaterialLabel _status = new();

    public TrustedDevicesPanel(AdminApi api, Guid userId)
    {
        _api = api; _userId = userId;
        Dock = DockStyle.Fill;

        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false; _list.Dock = DockStyle.Fill; _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add(L.HelloDevicesPanel_Device, 200);
        _list.Columns.Add(L.BootstrapView_Created, 130);
        _list.Columns.Add(L.TrustedDevicesPanel_Expires, 130);
        _list.Columns.Add(L.HelloDevicesPanel_LastUsed, 130);

        var tools = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(8, 6, 8, 6) };
        var refreshBtn = new MaterialButton { Text = L.AboutView_Refresh, AutoSize = true, Margin = new Padding(4, 0, 4, 0) };
        refreshBtn.Click += async (_, _) => await ShownAsync();
        var revokeBtn = new MaterialButton { Text = L.BootstrapView_Revoke, AutoSize = true, Margin = new Padding(4, 0, 4, 0), Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
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

    private TrustedDeviceInfo? Selected() => _list.SelectedItems.Count == 0 ? null : (TrustedDeviceInfo)_list.SelectedItems[0].Tag!;

    public async Task ShownAsync()
    {
        ThemeManager.StyleList(_list);
        try
        {
            var items = await _api.GetUserTrustsAsync(_userId);
            _list.Items.Clear();
            foreach (var t in items)
            {
                var it = new ListViewItem(string.IsNullOrWhiteSpace(t.DeviceName) ? "—" : t.DeviceName) { Tag = t };
                it.SubItems.Add(t.CreatedAt.LocalDateTime.ToString("g"));
                it.SubItems.Add(t.ExpiresAt.LocalDateTime.ToString("g"));
                it.SubItems.Add(t.LastUsedAt.LocalDateTime.ToString("g"));
                _list.Items.Add(it);
            }
            _status.Text = items.Count == 0 ? L.TrustedDevicesPanel_NoTrustedDevices : L.Format(L.TrustedDevicesPanel_TrustedDevice, items.Count);
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private async Task RevokeAsync()
    {
        if (Selected() is not { } t) return;
        var name = string.IsNullOrWhiteSpace(t.DeviceName) ? "—" : t.DeviceName;
        if (MessageBox.Show(L.Format(L.TrustedDevicesPanel_RevokeThisTrustedDevice, name),
                L.TrustedDevicesPanel_RevokeTrust, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.RevokeUserTrustAsync(_userId, t.Id); await ShownAsync(); }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }
}
