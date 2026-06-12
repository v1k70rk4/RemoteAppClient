using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient;

/// <summary>MSI legyártása egy csoporthoz egy csatornából, majd letöltése helyi fájlba.</summary>
public sealed class MsiForm : MaterialForm
{
    private readonly AdminApi _api;
    private readonly MaterialComboBox _group = new() { Hint = "Csoport" };
    private readonly MaterialComboBox _channel = new() { Hint = "Csatorna" };
    private readonly MaterialLabel _status = new();

    private sealed record GroupItem(Guid? Id, string Name) { public override string ToString() => Name; }

    public MsiForm(AdminApi api, List<GroupInfo> groups)
    {
        _api = api;
        ThemeManager.Skin.AddFormToManage(this);
        Text = "MSI gyártás";
        Sizable = false;
        Width = 470; Height = 340;
        StartPosition = FormStartPosition.CenterParent;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20, 16, 20, 8) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _group.Dock = DockStyle.Fill; _group.Margin = new Padding(3, 6, 3, 6);
        _group.Items.Add(new GroupItem(null, "(nincs csoport)"));
        foreach (var g in groups) _group.Items.Add(new GroupItem(g.Id, g.Name));
        _group.SelectedIndex = 0;

        _channel.Dock = DockStyle.Fill; _channel.Margin = new Padding(3, 6, 3, 6);
        _channel.Items.AddRange(["rtm", "beta"]); _channel.SelectedIndex = 0;

        var build = new MaterialButton { Text = "Gyártás és letöltés", AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(3, 10, 3, 6) };
        build.Click += async (_, _) => await BuildAsync();

        _status.Dock = DockStyle.Fill; _status.AutoSize = false; _status.Height = 50; _status.Margin = new Padding(3, 8, 3, 6);

        foreach (var c in new Control[] { _group, _channel, build, _status })
        {
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.Controls.Add(c);
        }

        Controls.Add(body);
    }

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
