using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>User trusted ("remember this device") machines: owner-drawn list with expiry + revoke, as a tab.</summary>
public sealed class TrustedDevicesPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly Guid _userId;
    private readonly OwnerList _list = new(44);
    private readonly MaterialLabel _status = new() { Dock = DockStyle.Fill };

    public TrustedDevicesPanel(AdminApi api, Guid userId)
    {
        _api = api; _userId = userId;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;
        Padding = new Padding(22, 14, 22, 12);

        _list.Dock = DockStyle.Fill;
        _list.SetColumns(
            new OwnerList.Col(L.HelloDevicesPanel_Device, 320),
            new OwnerList.Col(L.BootstrapView_Created, 170),
            new OwnerList.Col(L.TrustedDevicesPanel_Expires, 170),
            new OwnerList.Col(L.HelloDevicesPanel_LastUsed, 170));
        _list.PaintRow += (_, e) =>
        {
            var t = (TrustedDeviceInfo)e.Item;
            e.Text(0, string.IsNullOrWhiteSpace(t.DeviceName) ? "—" : t.DeviceName, UiFont.Body, ThemeManager.Text);
            e.Text(1, t.CreatedAt.LocalDateTime.ToString("g"), UiFont.MonoSmall, ThemeManager.Text3);
            e.Text(2, t.ExpiresAt.LocalDateTime.ToString("g"), UiFont.MonoSmall, ThemeManager.Text3);
            e.Text(3, t.LastUsedAt.LocalDateTime.ToString("g"), UiFont.MonoSmall, ThemeManager.Text3);
        };

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = ThemeManager.Bg };
        var refreshBtn = new UiButton(L.AboutView_Refresh, UiButton.Style.Outline) { Location = new Point(0, 6) };
        refreshBtn.Click += async (_, _) => await ShownAsync();
        var revokeBtn = new UiButton(L.BootstrapView_Revoke, UiButton.Style.Warn) { Location = new Point(refreshBtn.Width + 8, 6) };
        revokeBtn.Click += async (_, _) => await RevokeAsync();
        actions.Controls.Add(refreshBtn);
        actions.Controls.Add(revokeBtn);

        var statusHost = new Panel { Dock = DockStyle.Bottom, Height = 22, BackColor = ThemeManager.Bg };
        statusHost.Controls.Add(_status);

        Controls.Add(_list);
        Controls.Add(actions);
        Controls.Add(statusHost);
    }

    private TrustedDeviceInfo? Selected() => _list.Selected as TrustedDeviceInfo;

    public async Task ShownAsync()
    {
        try
        {
            var items = await _api.GetUserTrustsAsync(_userId);
            _list.BeginUpdate();
            _list.Clear();
            foreach (var t in items) _list.Add(t);
            _list.EndUpdate();
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
