using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient;

/// <summary>Egy eszköz admin-mezőinek szerkesztése: csoport, flagek, megjegyzés.</summary>
public sealed class EditDeviceForm : MaterialForm
{
    private readonly MaterialComboBox _group = new();
    private readonly MaterialSwitch _update = new();
    private readonly MaterialSwitch _beta = new();
    private readonly MaterialComboBox _unattended = new();
    private readonly MaterialComboBox _consent = new();
    private readonly MaterialMultiLineTextBox2 _note = new();

    public DeviceUpdate? Result { get; private set; }

    private sealed record GroupItem(Guid? Id, string Name) { public override string ToString() => Name; }

    public EditDeviceForm(DeviceInfo d, List<GroupInfo> groups)
    {
        ThemeManager.Skin.AddFormToManage(this);
        Text = $"Eszköz: {(string.IsNullOrEmpty(d.Hostname) ? d.DeviceId : d.Hostname)}";
        Sizable = false;
        Width = 460; Height = 540;
        StartPosition = FormStartPosition.CenterParent;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(18, 12, 18, 12) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

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
        _note.Dock = DockStyle.Fill; _note.Height = 90;

        Row(body, "Csoport", _group);
        Row(body, "Frissíthető", _update);
        Row(body, "BETA csatorna", _beta);
        Row(body, "Unattended", _unattended);
        Row(body, "Hozzájárulás kell", _consent);
        Row(body, "Megjegyzés", _note);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 56, Padding = new Padding(0, 8, 16, 8) };
        var ok = new MaterialButton { Text = "Mentés", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new MaterialButton { Text = "Mégse", DialogResult = DialogResult.Cancel, AutoSize = true, Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        ok.Click += (_, _) => BuildResult();
        buttons.Controls.AddRange([ok, cancel]);

        Controls.Add(body);
        Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;
    }

    private static void Row(TableLayoutPanel t, string label, Control control)
    {
        int r = t.RowCount;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var lbl = new MaterialLabel { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 12, 3, 3) };
        if (control is not MaterialMultiLineTextBox2) control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(3, 6, 3, 6);
        t.Controls.Add(lbl, 0, r);
        t.Controls.Add(control, 1, r);
        t.RowCount = r + 1;
    }

    private static void SetupTri(MaterialComboBox combo, bool? value)
    {
        combo.Items.AddRange(["örökli", "igen", "nem"]);
        combo.SelectedIndex = value switch { null => 0, true => 1, false => 2 };
    }

    private static bool? FromTri(MaterialComboBox combo) => combo.SelectedIndex switch { 1 => true, 2 => false, _ => null };

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
