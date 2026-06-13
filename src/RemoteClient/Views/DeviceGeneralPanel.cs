using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient.Views;

/// <summary>Egy eszköz admin-mezői (csoport, flagek, megjegyzés) beágyazva, Mentés gombbal — a szerkesztő „Általános" füle.</summary>
public sealed class DeviceGeneralPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly string _deviceId;
    private readonly MaterialComboBox _group = new() { Width = 240 };
    private readonly MaterialSwitch _update = new() { Text = "Frissíthető", AutoSize = true };
    private readonly MaterialSwitch _beta = new() { Text = "BETA csatorna", AutoSize = true };
    private readonly MaterialComboBox _unattended = new() { Hint = "Felügyelet nélküli hozzáférés", Width = 240 };
    private readonly MaterialComboBox _consent = new() { Hint = "Hozzájárulás kell", Width = 240 };
    private readonly MaterialMultiLineTextBox2 _note = new() { Width = 380, Height = 80 };
    private readonly MaterialLabel _status = new() { AutoSize = true, Margin = new Padding(4, 12, 0, 0) };

    private sealed record GroupItem(Guid? Id, string Name) { public override string ToString() => Name; }

    public DeviceGeneralPanel(AdminApi api, DeviceInfo d, List<GroupInfo> groups)
    {
        _api = api; _deviceId = d.DeviceId;
        Dock = DockStyle.Fill;

        _group.Items.Add(new GroupItem(null, "(nincs)"));
        foreach (var g in groups) _group.Items.Add(new GroupItem(g.Id, g.Name));
        _group.SelectedIndex = 0;
        for (int i = 0; i < _group.Items.Count; i++)
            if (_group.Items[i] is GroupItem gi && gi.Id == d.GroupId) { _group.SelectedIndex = i; break; }

        _update.Checked = d.UpdateAllowed;
        _beta.Checked = string.Equals(d.Channel, "beta", StringComparison.OrdinalIgnoreCase);
        SetupTri(_unattended, d.UnattendedAllowed);
        SetupTri(_consent, d.ConsentRequired);
        _note.Text = d.Note ?? "";

        var save = ViewUi.ToolbarButton("Mentés");
        save.Click += async (_, _) => await SaveAsync();

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 10, 12, 8) };
        void Lbl(string t) => body.Controls.Add(new MaterialLabel { Text = t, FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, Margin = new Padding(4, 10, 0, 0) });
        Lbl("Csoport"); body.Controls.Add(_group);
        body.Controls.Add(_update);
        body.Controls.Add(_beta);
        Lbl("Felügyelet nélküli hozzáférés (unattended)"); body.Controls.Add(_unattended);
        Lbl("Hozzájárulás kell (consent)"); body.Controls.Add(_consent);
        Lbl("Megjegyzés"); body.Controls.Add(_note);
        body.Controls.Add(save);
        body.Controls.Add(_status);
        Controls.Add(body);
    }

    private static void SetupTri(MaterialComboBox combo, bool? value)
    {
        combo.Items.AddRange(["örökli", "igen", "nem"]);
        combo.SelectedIndex = value switch { null => 0, true => 1, false => 2 };
    }

    private static bool? FromTri(MaterialComboBox combo) => combo.SelectedIndex switch { 1 => true, 2 => false, _ => null };

    private async Task SaveAsync()
    {
        try
        {
            var upd = new DeviceUpdate
            {
                GroupId = ((GroupItem)_group.SelectedItem!).Id ?? Guid.Empty, // Empty → a szerver null-ra állítja
                UpdateAllowed = _update.Checked,
                Channel = _beta.Checked ? "beta" : "rtm",
                UnattendedAllowed = FromTri(_unattended),
                ConsentRequired = FromTri(_consent),
                Note = _note.Text,
            };
            await _api.UpdateDeviceAsync(_deviceId, upd);
            _status.Text = "Mentve.";
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }
}
