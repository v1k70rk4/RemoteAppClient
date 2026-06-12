using RemoteAgent.Admin;

namespace RemoteClient;

/// <summary>Egy eszköz admin-mezőinek szerkesztése: csoport, flagek, megjegyzés.</summary>
public sealed class EditDeviceForm : Form
{
    private readonly ComboBox _group = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _update = new();
    private readonly CheckBox _beta = new();
    private readonly ComboBox _unattended = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _consent = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _note = new() { Multiline = true };

    public DeviceUpdate? Result { get; private set; }

    private sealed record GroupItem(Guid? Id, string Name) { public override string ToString() => Name; }

    public EditDeviceForm(DeviceInfo d, List<GroupInfo> groups)
    {
        Text = $"Eszköz: {(string.IsNullOrEmpty(d.Hostname) ? d.DeviceId : d.Hostname)}";
        Width = 430; Height = 420;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;

        int y = 18;
        AddLabel("Csoport:", y);
        _group.SetBounds(150, y, 250, 24);
        _group.Items.Add(new GroupItem(null, "(nincs)"));
        foreach (var g in groups) _group.Items.Add(new GroupItem(g.Id, g.Name));
        _group.SelectedIndex = 0;
        for (int i = 0; i < _group.Items.Count; i++)
            if (_group.Items[i] is GroupItem gi && gi.Id == d.GroupId) { _group.SelectedIndex = i; break; }
        Controls.Add(_group); y += 38;

        AddLabel("Frissíthető:", y);
        _update.SetBounds(150, y, 24, 24);
        _update.Checked = d.UpdateAllowed;
        Controls.Add(_update); y += 38;

        AddLabel("BETA csatorna:", y);
        _beta.SetBounds(150, y, 24, 24);
        _beta.Checked = string.Equals(d.Channel, "beta", StringComparison.OrdinalIgnoreCase);
        Controls.Add(_beta); y += 38;

        AddLabel("Unattended:", y);
        SetupTri(_unattended, d.UnattendedAllowed, y); y += 38;

        AddLabel("Hozzájárulás kell:", y);
        SetupTri(_consent, d.ConsentRequired, y); y += 38;

        AddLabel("Megjegyzés:", y);
        _note.SetBounds(150, y, 250, 72);
        _note.Text = d.Note ?? "";
        Controls.Add(_note); y += 84;

        var ok = new Button { Text = "Mentés", DialogResult = DialogResult.OK };
        ok.SetBounds(214, y, 88, 30);
        ok.Click += (_, _) => BuildResult();
        var cancel = new Button { Text = "Mégse", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(310, y, 88, 30);
        Controls.AddRange([ok, cancel]);
        AcceptButton = ok; CancelButton = cancel;
    }

    private void AddLabel(string text, int y) =>
        Controls.Add(new Label { Text = text, AutoSize = false, Bounds = new Rectangle(16, y + 3, 128, 22) });

    private void SetupTri(ComboBox combo, bool? value, int y)
    {
        combo.SetBounds(150, y, 120, 24);
        combo.Items.AddRange(["örökli", "igen", "nem"]);
        combo.SelectedIndex = value switch { null => 0, true => 1, false => 2 };
        Controls.Add(combo);
    }

    private static bool? FromTri(ComboBox combo) => combo.SelectedIndex switch { 1 => true, 2 => false, _ => null };

    private void BuildResult()
    {
        Result = new DeviceUpdate
        {
            GroupId = ((GroupItem)_group.SelectedItem!).Id ?? Guid.Empty, // Empty → a szerver null-ra állítja
            UpdateAllowed = _update.Checked,
            Channel = _beta.Checked ? "beta" : "rtm",
            UnattendedAllowed = FromTri(_unattended),
            ConsentRequired = FromTri(_consent),
            Note = _note.Text,
        };
    }
}
