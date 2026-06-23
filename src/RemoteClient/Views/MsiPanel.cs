using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>Builds and downloads an MSI for a group from a channel — a card with group/channel + two toggles
/// + Build &amp; download. Embedded in the Channels MSI build editor. See design_handoff_console_redesign.</summary>
public sealed class MsiPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly UiCombo _group = new(504);
    private readonly UiCombo _channel = new(504);
    private readonly UiToggle _client = new() { Checked = true };
    private readonly UiToggle _shortcut = new() { Checked = true };
    private readonly MaterialLabel _status = new() { AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(2, 10, 0, 0) };

    private sealed record GroupItem(Guid? Id, string Name) { public override string ToString() => Name; }

    public MsiPanel(AdminApi api, List<GroupInfo> groups)
    {
        _api = api;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;

        _group.Items.Add(new GroupItem(null, L.BootstrapView_NoGroup));
        foreach (var g in groups) _group.Items.Add(new GroupItem(g.Id, g.Name));
        _group.SelectedIndex = 0;
        _channel.Items.AddRange(["rtm", "beta"]); _channel.SelectedIndex = 0;
        _client.CheckedChanged += (_, _) => _shortcut.Enabled = _client.Checked;

        var build = new UiButton(L.MsiPanel_BuildAndDownload);
        build.Click += async (_, _) => await BuildAsync();

        const int cardW = 540, cw = cardW - 36;
        build.Width = cw;
        var body = new Panel();
        _group.Location = new Point(0, 22);
        _channel.Location = new Point(0, 84);
        body.Controls.Add(_group);
        body.Controls.Add(_channel);
        body.Controls.Add(new SettingRow(L.MsiPanel_InstallConsoleClient, "", _client) { Location = new Point(0, 132), Size = new Size(cw, 48) });
        body.Controls.Add(new SettingRow(L.MsiPanel_StartMenuShortcutForThe, "", _shortcut) { Location = new Point(0, 180), Size = new Size(cw, 48) });
        build.Location = new Point(0, 242);
        _status.Location = new Point(2, 290);
        body.Controls.Add(build);
        body.Controls.Add(_status);
        body.Paint += (_, e) =>
        {
            void Lbl(string t, int y) => TextRenderer.DrawText(e.Graphics, t, UiFont.Label, new Rectangle(0, y, cw, 16), ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding);
            Lbl(L.BootstrapView_Group, 2);
            Lbl(L.DeviceTelemetryPanel_Channel, 64);
        };

        Controls.Add(new Card(null, null, body) { Width = cardW, Height = 322, Location = new Point(0, 8) });
    }

    private async Task BuildAsync()
    {
        try
        {
            Enabled = false; _status.Text = L.MsiPanel_BuildingMSIOnTheServer;
            var groupId = ((GroupItem)_group.SelectedItem!).Id;
            var (fileName, _) = await _api.BuildMsiAsync(groupId, (string)_channel.SelectedItem!, _client.Checked, _client.Checked && _shortcut.Checked);

            using var sd = new SaveFileDialog { FileName = fileName, Filter = "MSI|*.msi" };
            if (sd.ShowDialog(this) == DialogResult.OK)
            {
                _status.Text = L.MsiPanel_Downloading;
                await _api.DownloadMsiAsync(fileName, sd.FileName);
                _status.Text = L.MsiPanel_Done + sd.FileName;
            }
            else _status.Text = L.MsiPanel_BuiltOnTheServer + fileName + L.MsiPanel_DownloadSkipped;
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
        finally { Enabled = true; }
    }
}
