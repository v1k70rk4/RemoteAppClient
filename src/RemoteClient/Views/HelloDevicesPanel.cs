using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>User Windows Hello devices (owner-drawn list + revoke), embedded in the Windows Hello tab.</summary>
public sealed class HelloDevicesPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly Guid _userId;
    private readonly OwnerList _list = new(44);
    private readonly MaterialLabel _status = new() { Dock = DockStyle.Fill };

    public HelloDevicesPanel(AdminApi api, Guid userId)
    {
        _api = api; _userId = userId;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;
        Padding = new Padding(22, 14, 22, 12);

        _list.Dock = DockStyle.Fill;
        _list.SetColumns(
            new OwnerList.Col(L.HelloDevicesPanel_Device, 380),
            new OwnerList.Col(L.BootstrapView_Created, 200),
            new OwnerList.Col(L.HelloDevicesPanel_LastUsed, 200));
        _list.PaintRow += (_, e) =>
        {
            var c = (HelloCredentialInfo)e.Item;
            e.Text(0, c.DeviceName, UiFont.Body, ThemeManager.Text);
            e.Text(1, c.CreatedAt.LocalDateTime.ToString("g"), UiFont.MonoSmall, ThemeManager.Text3);
            e.Text(2, c.LastUsedAt?.LocalDateTime.ToString("g") ?? "—", UiFont.MonoSmall, ThemeManager.Text3);
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

    private HelloCredentialInfo? Selected() => _list.Selected as HelloCredentialInfo;

    public async Task ShownAsync()
    {
        try
        {
            var creds = await _api.GetUserHelloAsync(_userId);
            _list.BeginUpdate();
            _list.Clear();
            foreach (var c in creds) _list.Add(c);
            _list.EndUpdate();
            _status.Text = creds.Count == 0 ? L.HelloDevicesPanel_NoWindowsHelloDevicesFor : L.Format(L.HelloDevicesPanel_HelloDevice, creds.Count);
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private async Task RevokeAsync()
    {
        if (Selected() is not { } c) return;
        if (MessageBox.Show(L.Format(L.HelloDevicesPanel_RevokeThisWindowsHelloDevice, c.DeviceName),
                L.HelloDevicesPanel_RevokeHello, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.RevokeUserHelloAsync(_userId, c.Id); await ShownAsync(); }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }
}
