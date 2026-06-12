using RemoteAgent.Admin;

namespace RemoteClient;

/// <summary>MSI legyártása egy csoporthoz egy csatornából, majd letöltése helyi fájlba.</summary>
public sealed class MsiForm : Form
{
    private readonly AdminApi _api;
    private readonly ComboBox _group = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _channel = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _status = new();

    private sealed record GroupItem(Guid? Id, string Name) { public override string ToString() => Name; }

    public MsiForm(AdminApi api, List<GroupInfo> groups)
    {
        _api = api;
        Text = "MSI gyártás";
        Width = 470; Height = 230;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        AddLabel("Csoport:", 12, 18);
        _group.SetBounds(100, 14, 250, 24);
        _group.Items.Add(new GroupItem(null, "(nincs csoport)"));
        foreach (var g in groups) _group.Items.Add(new GroupItem(g.Id, g.Name));
        _group.SelectedIndex = 0;

        AddLabel("Csatorna:", 12, 52);
        _channel.SetBounds(100, 48, 120, 24); _channel.Items.AddRange(["rtm", "beta"]); _channel.SelectedIndex = 0;

        var build = new Button { Text = "Gyártás és letöltés", Bounds = new Rectangle(100, 90, 170, 32) };
        build.Click += async (_, _) => await BuildAsync();

        _status.SetBounds(12, 134, 440, 50);

        Controls.AddRange([_group, _channel, build, _status]);
    }

    private void AddLabel(string t, int x, int y) =>
        Controls.Add(new Label { Text = t, Bounds = new Rectangle(x, y + 3, 86, 22) });

    private async Task BuildAsync()
    {
        try
        {
            Enabled = false; _status.Text = "MSI gyártása a szerveren…";
            var groupId = ((GroupItem)_group.SelectedItem!).Id;
            var (fileName, _) = await _api.BuildMsiAsync(groupId, (string)_channel.SelectedItem!);

            using var sd = new SaveFileDialog { FileName = fileName, Filter = "MSI|*.msi" };
            if (sd.ShowDialog(this) == DialogResult.OK)
            {
                _status.Text = "Letöltés…";
                await _api.DownloadMsiAsync(fileName, sd.FileName);
                _status.Text = "Kész: " + sd.FileName;
            }
            else
            {
                _status.Text = "Legyártva a szerveren: " + fileName + " (letöltés kihagyva)";
            }
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
        finally { Enabled = true; }
    }
}
