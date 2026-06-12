namespace RemoteClient;

/// <summary>Egy exe feltöltése egy release-csatornára (agent/updater komponensként).</summary>
public sealed class UploadPackageForm : Form
{
    private readonly AdminApi _api;
    private readonly TextBox _file = new() { ReadOnly = true };
    private readonly ComboBox _channel = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _component = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _version = new();
    private readonly Label _status = new();

    public UploadPackageForm(AdminApi api)
    {
        _api = api;
        Text = "Exe feltöltése csatornára";
        Width = 470; Height = 260;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        AddLabel("Fájl:", 12, 18);
        _file.SetBounds(100, 14, 240, 24);
        var browse = new Button { Text = "…", Bounds = new Rectangle(346, 13, 40, 26) };
        browse.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "Exe|*.exe" };
            if (d.ShowDialog(this) == DialogResult.OK) { _file.Text = d.FileName; if (string.IsNullOrWhiteSpace(_version.Text)) TryFillVersion(d.FileName); }
        };

        AddLabel("Csatorna:", 12, 52);
        _channel.SetBounds(100, 48, 120, 24); _channel.Items.AddRange(["rtm", "beta"]); _channel.SelectedIndex = 1;

        AddLabel("Komponens:", 12, 86);
        _component.SetBounds(100, 82, 120, 24); _component.Items.AddRange(["agent", "updater"]); _component.SelectedIndex = 0;

        AddLabel("Verzió:", 12, 120);
        _version.SetBounds(100, 116, 120, 24);

        var ok = new Button { Text = "Feltöltés", Bounds = new Rectangle(214, 152, 100, 30) };
        ok.Click += async (_, _) => await UploadAsync();
        var cancel = new Button { Text = "Mégse", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(322, 152, 80, 30) };

        _status.SetBounds(12, 196, 440, 24);

        Controls.AddRange([_file, browse, _channel, _component, _version, ok, cancel, _status]);
        CancelButton = cancel;
    }

    private void AddLabel(string t, int x, int y) =>
        Controls.Add(new Label { Text = t, Bounds = new Rectangle(x, y + 3, 86, 22) });

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
