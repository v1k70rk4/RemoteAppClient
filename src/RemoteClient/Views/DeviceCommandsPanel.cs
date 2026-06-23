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
    private readonly UiButton _restart = new(L.DeviceCommandsPanel_Restart, UiButton.Style.Outline);
    private readonly UiButton _forceRestart = new(L.DeviceCommandsPanel_ForceRestart, UiButton.Style.Danger);
    private readonly UiButton _cancel = new(L.DeviceCommandsPanel_CancelRestart, UiButton.Style.Outline);
    private readonly UiButton _logout = new(L.DeviceCommandsPanel_Logout, UiButton.Style.Outline);
    private readonly MaterialLabel _status = new() { AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(4, 12, 0, 0) };

    public DeviceCommandsPanel(AdminApi api, DeviceInfo d)
    {
        _api = api; _deviceId = d.DeviceId;
        _host = string.IsNullOrEmpty(d.Hostname) ? d.DeviceId : d.Hostname;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;
        Padding = new Padding(16);

        _restart.Click += async (_, _) => await RunAsync("restart", L.Format(L.DeviceCommandsPanel_ConfirmRestart, _host));
        _forceRestart.Click += async (_, _) => await RunAsync("force-restart", L.Format(L.DeviceCommandsPanel_ConfirmForceRestart, _host));
        _cancel.Click += async (_, _) => await RunAsync("cancel", null); // a safe undo, no confirmation
        _logout.Click += async (_, _) => await RunAsync("logout", L.Format(L.DeviceCommandsPanel_ConfirmLogout, _host));

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new ActionCard(L.DeviceCommandsPanel_Restart, L.DeviceCommandsPanel_RestartDesc, _restart) { Dock = DockStyle.Fill }, 0, 0);
        grid.Controls.Add(new ActionCard(L.DeviceCommandsPanel_ForceRestart, L.DeviceCommandsPanel_ForceRestartDesc, _forceRestart) { Dock = DockStyle.Fill }, 1, 0);
        grid.Controls.Add(new ActionCard(L.DeviceCommandsPanel_CancelRestart, L.DeviceCommandsPanel_CancelDesc, _cancel) { Dock = DockStyle.Fill }, 0, 1);
        grid.Controls.Add(new ActionCard(L.DeviceCommandsPanel_Logout, L.DeviceCommandsPanel_LogoutDesc, _logout) { Dock = DockStyle.Fill }, 1, 1);
        _status.Margin = new Padding(6, 12, 0, 0);
        grid.Controls.Add(_status, 0, 2);
        grid.SetColumnSpan(_status, 2);
        Controls.Add(grid);
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
