using System.Drawing;
using MaterialSkin.Controls;

namespace RemoteClient;

/// <summary>Egy exe feltöltése egy release-csatornára (agent/updater komponensként).</summary>
public sealed class UploadPackageForm : MaterialForm
{
    private readonly AdminApi _api;
    private readonly MaterialTextBox2 _file = new() { Hint = "Fájl", ReadOnly = true };
    private readonly MaterialComboBox _channel = new() { Hint = "Csatorna" };
    private readonly MaterialComboBox _component = new() { Hint = "Komponens" };
    private readonly MaterialTextBox2 _version = new() { Hint = "Verzió" };
    private readonly MaterialLabel _status = new();

    public UploadPackageForm(AdminApi api)
    {
        _api = api;
        ThemeManager.Skin.AddFormToManage(this);
        Text = "Exe feltöltése csatornára";
        Sizable = false;
        Width = 470; Height = 420;
        StartPosition = FormStartPosition.CenterParent;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20, 16, 20, 8) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var fileRow = new Panel { Dock = DockStyle.Fill, Height = 52, Margin = new Padding(3, 6, 3, 6) };
        var browse = new MaterialButton { Text = "…", Dock = DockStyle.Right, Width = 48, Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        _file.Dock = DockStyle.Fill;
        browse.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "Exe|*.exe" };
            if (d.ShowDialog(this) == DialogResult.OK) { _file.Text = d.FileName; if (string.IsNullOrWhiteSpace(_version.Text)) TryFillVersion(d.FileName); }
        };
        fileRow.Controls.Add(_file);
        fileRow.Controls.Add(browse);

        _channel.Dock = DockStyle.Fill; _channel.Margin = new Padding(3, 6, 3, 6);
        _channel.Items.AddRange(["rtm", "beta"]); _channel.SelectedIndex = 1;
        _component.Dock = DockStyle.Fill; _component.Margin = new Padding(3, 6, 3, 6);
        _component.Items.AddRange(["agent", "updater"]); _component.SelectedIndex = 0;
        _version.Dock = DockStyle.Fill; _version.Margin = new Padding(3, 6, 3, 6);

        foreach (var c in new Control[] { fileRow, _channel, _component, _version })
        {
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.Controls.Add(c);
        }
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _status.Dock = DockStyle.Fill; _status.AutoSize = false; _status.Height = 28; _status.Margin = new Padding(3, 8, 3, 6);
        body.Controls.Add(_status);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 56, Padding = new Padding(0, 8, 16, 8) };
        var ok = new MaterialButton { Text = "Feltöltés", AutoSize = true };
        ok.Click += async (_, _) => await UploadAsync();
        var cancel = new MaterialButton { Text = "Mégse", DialogResult = DialogResult.Cancel, AutoSize = true, Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        buttons.Controls.AddRange([ok, cancel]);

        Controls.Add(body);
        Controls.Add(buttons);
        CancelButton = cancel;
    }

    private void TryFillVersion(string path)
    {
        try { var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(path); _version.Text = $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}"; }
        catch { /* nem baj */ }
    }

    private async Task UploadAsync()
    {
        if (!File.Exists(_file.Text)) { _status.Text = "Válassz egy exe fájlt."; return; }
        if (string.IsNullOrWhiteSpace(_version.Text)) { _status.Text = "Adj meg verziót."; return; }
        try
        {
            Enabled = false; _status.Text = "Feltöltés folyamatban…";
            await _api.UploadPackageAsync((string)_channel.SelectedItem!, (string)_component.SelectedItem!, _version.Text.Trim(), _file.Text);
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; Enabled = true; }
    }
}
