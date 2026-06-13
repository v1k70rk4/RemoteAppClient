using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient.Views;

/// <summary>MSI gyártása egy csoporthoz egy csatornából + letöltés — beágyazható panel (a Csatornák „MSI gyártás" füle).</summary>
public sealed class MsiPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly MaterialComboBox _group = new() { Hint = "Csoport", Width = 320 };
    private readonly MaterialComboBox _channel = new() { Hint = "Csatorna", Width = 200 };
    private readonly MaterialSwitch _client = new() { Text = "Konzol-kliens telepítése", Checked = true, AutoSize = true };
    private readonly MaterialSwitch _shortcut = new() { Text = "Start menü parancsikon a klienshez", Checked = true, AutoSize = true };
    private readonly MaterialLabel _status = new() { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(4, 12, 0, 0) };

    private sealed record GroupItem(Guid? Id, string Name) { public override string ToString() => Name; }

    public MsiPanel(AdminApi api, List<GroupInfo> groups)
    {
        _api = api;
        Dock = DockStyle.Fill;

        _group.Items.Add(new GroupItem(null, "(nincs csoport)"));
        foreach (var g in groups) _group.Items.Add(new GroupItem(g.Id, g.Name));
        _group.SelectedIndex = 0;
        _channel.Items.AddRange(["rtm", "beta"]); _channel.SelectedIndex = 0;
        _client.CheckedChanged += (_, _) => _shortcut.Enabled = _client.Checked;

        var build = ViewUi.ToolbarButton("Gyártás és letöltés");
        build.Click += async (_, _) => await BuildAsync();

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 10, 12, 8) };
        void Lbl(string t) => body.Controls.Add(new MaterialLabel { Text = t, FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, Margin = new Padding(4, 10, 0, 0) });
        Lbl("Csoport"); _group.Margin = new Padding(4, 4, 4, 8); body.Controls.Add(_group);
        Lbl("Csatorna"); _channel.Margin = new Padding(4, 4, 4, 8); body.Controls.Add(_channel);
        _client.Margin = new Padding(4, 8, 4, 4); body.Controls.Add(_client);
        _shortcut.Margin = new Padding(4, 0, 4, 12); body.Controls.Add(_shortcut);
        body.Controls.Add(build);
        body.Controls.Add(_status);
        Controls.Add(body);
    }

    private async Task BuildAsync()
    {
        try
        {
            Enabled = false; _status.Text = "MSI gyártása a szerveren…";
            var groupId = ((GroupItem)_group.SelectedItem!).Id;
            var (fileName, _) = await _api.BuildMsiAsync(groupId, (string)_channel.SelectedItem!, _client.Checked, _client.Checked && _shortcut.Checked);

            using var sd = new SaveFileDialog { FileName = fileName, Filter = "MSI|*.msi" };
            if (sd.ShowDialog(this) == DialogResult.OK)
            {
                _status.Text = "Letöltés…";
                await _api.DownloadMsiAsync(fileName, sd.FileName);
                _status.Text = "Kész: " + sd.FileName;
            }
            else _status.Text = "Legyártva a szerveren: " + fileName + " (letöltés kihagyva)";
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
        finally { Enabled = true; }
    }
}
