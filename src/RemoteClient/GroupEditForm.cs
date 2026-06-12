using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient;

/// <summary>Egy eszközcsoport létrehozása/szerkesztése: név + consent/unattended alapértelmezések.</summary>
public sealed class GroupEditForm : MaterialForm
{
    private readonly MaterialTextBox2 _name = new() { Hint = "Csoport neve" };
    private readonly MaterialSwitch _consent = new() { Text = "Hozzájárulás kell megtekintéshez" };
    private readonly MaterialSwitch _unattended = new() { Text = "Unattended (felügyelet nélküli) engedélyezve" };

    public GroupInfo Result { get; private set; } = new();

    public GroupEditForm(GroupInfo? existing = null)
    {
        ThemeManager.Skin.AddFormToManage(this);
        Text = existing is null ? "Új csoport" : "Csoport szerkesztése";
        Sizable = false;
        Width = 440; Height = 320;
        StartPosition = FormStartPosition.CenterParent;

        if (existing is not null)
        {
            _name.Text = existing.Name; _consent.Checked = existing.ConsentRequired; _unattended.Checked = existing.UnattendedAllowed;
            Result = new GroupInfo { Id = existing.Id };
        }
        else { _unattended.Checked = true; }

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20, 16, 20, 8) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var c in new Control[] { _name, _consent, _unattended })
        {
            c.Dock = DockStyle.Top; c.Margin = new Padding(3, 8, 3, 8);
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.Controls.Add(c);
        }

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 56, Padding = new Padding(0, 8, 16, 8) };
        var ok = new MaterialButton { Text = "Mentés", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new MaterialButton { Text = "Mégse", DialogResult = DialogResult.Cancel, AutoSize = true, Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text)) { DialogResult = DialogResult.None; MessageBox.Show("Adj meg egy nevet."); return; }
            Result.Name = _name.Text.Trim();
            Result.ConsentRequired = _consent.Checked;
            Result.UnattendedAllowed = _unattended.Checked;
        };
        buttons.Controls.AddRange([ok, cancel]);

        Controls.Add(body);
        Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;
    }
}
