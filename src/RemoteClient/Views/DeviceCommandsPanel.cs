using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>Device Commands tab: power actions (restart / force restart / cancel restart / log off user).</summary>
public sealed class DeviceCommandsPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly string _deviceId;
    private readonly string _host;
    private readonly MaterialButton _restart = ViewUi.ToolbarButton(L.DeviceCommandsPanel_Restart);
    private readonly MaterialButton _forceRestart = ViewUi.ToolbarButton(L.DeviceCommandsPanel_ForceRestart, primary: false);
    private readonly MaterialButton _cancel = ViewUi.ToolbarButton(L.DeviceCommandsPanel_CancelRestart, primary: false);
    private readonly MaterialButton _logout = ViewUi.ToolbarButton(L.DeviceCommandsPanel_Logout, primary: false);
    private readonly MaterialLabel _status = new() { AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(4, 12, 0, 0) };

    public DeviceCommandsPanel(AdminApi api, DeviceInfo d)
    {
        _api = api; _deviceId = d.DeviceId;
        _host = string.IsNullOrEmpty(d.Hostname) ? d.DeviceId : d.Hostname;
        Dock = DockStyle.Fill;

        _restart.Click += async (_, _) => await RunAsync("restart", L.Format(L.DeviceCommandsPanel_ConfirmRestart, _host));
        _forceRestart.Click += async (_, _) => await RunAsync("force-restart", L.Format(L.DeviceCommandsPanel_ConfirmForceRestart, _host));
        _cancel.Click += async (_, _) => await RunAsync("cancel", null); // a safe undo, no confirmation
        _logout.Click += async (_, _) => await RunAsync("logout", L.Format(L.DeviceCommandsPanel_ConfirmLogout, _host));

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 10, 12, 8) };
        void Lbl(string t) => body.Controls.Add(new MaterialLabel { Text = t, FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(4, 10, 0, 0) });

        Lbl(L.DeviceCommandsPanel_Help);
        body.Controls.Add(_restart);
        body.Controls.Add(_forceRestart);
        body.Controls.Add(_cancel);
        body.Controls.Add(new MaterialDivider { Width = 440, Margin = new Padding(4, 16, 4, 8) });
        body.Controls.Add(_logout);
        body.Controls.Add(_status);
        Controls.Add(body);
    }

    private async Task RunAsync(string action, string? confirm)
    {
        if (confirm is not null &&
            MessageBox.Show(confirm, L.DeviceCommandsPanel_Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        _status.Text = L.DeviceCommandsPanel_Sending;
        try
        {
            var outcome = await PollAsync(await _api.PowerAsync(_deviceId, action));
            _status.Text = outcome switch
            {
                "scheduled" => L.DeviceCommandsPanel_Scheduled,
                "cancelled" => L.DeviceCommandsPanel_Cancelled,
                "logged-out" => L.DeviceCommandsPanel_LoggedOut,
                "no-user" => L.DeviceCommandsPanel_NoUser,
                "failed" => L.DeviceCommandsPanel_Failed,
                _ => L.DeviceCommandsPanel_NoAnswer,
            };
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    /// <summary>Polls the access-result for the nonce for ~15s.</summary>
    private async Task<string> PollAsync(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce)) return "";
        for (int i = 0; i < 15; i++)
        {
            try { var o = await _api.GetAccessResultAsync(nonce); if (!string.IsNullOrEmpty(o)) return o; }
            catch { /* transient */ }
            await Task.Delay(1000);
        }
        return "";
    }
}
