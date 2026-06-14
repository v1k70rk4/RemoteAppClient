using System.Drawing;
using MaterialSkin.Controls;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient;

/// <summary>New user data; the server generates a temporary password.</summary>
public sealed class NewUserForm : MaterialForm
{
    private readonly MaterialTextBox2 _username = new() { Hint = L.ForgotPasswordForm_001 };
    private readonly MaterialTextBox2 _name = new() { Hint = L.NewUserForm_001 };
    private readonly MaterialTextBox2 _email = new() { Hint = L.NewUserForm_002 };
    private readonly MaterialComboBox _role = new() { Hint = L.NewUserForm_008 };
    private readonly MaterialSwitch _emailCode = new() { Text = L.NewUserForm_003, AutoSize = true, Checked = true };

    public string Username => _username.Text.Trim();
    public string? FullName => string.IsNullOrWhiteSpace(_name.Text) ? null : _name.Text.Trim();
    public string? Email => string.IsNullOrWhiteSpace(_email.Text) ? null : _email.Text.Trim();
    public string Role => (string)_role.SelectedItem!;
    public bool EmailCode => _emailCode.Checked;

    public NewUserForm()
    {
        ThemeManager.Skin.AddFormToManage(this);
        Text = L.NewUserForm_004;
        Sizable = false;
        Width = 420; Height = 430;
        StartPosition = FormStartPosition.CenterParent;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20, 16, 20, 8) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var c in new Control[] { _username, _name, _email, _role, _emailCode })
        {
            c.Dock = DockStyle.Top; c.Margin = new Padding(3, 6, 3, 6);
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.Controls.Add(c);
        }
        _role.Items.AddRange(["operator", "admin"]); _role.SelectedIndex = 0;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 56, Padding = new Padding(0, 8, 16, 8) };
        var ok = new MaterialButton { Text = L.NewUserForm_005, DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new MaterialButton { Text = L.ConsentWaitForm_004, DialogResult = DialogResult.Cancel, AutoSize = true, Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        ok.Click += (_, _) =>
        {
            if (Username.Length == 0) { DialogResult = DialogResult.None; MessageBox.Show(L.NewUserForm_006); return; }
            if (string.IsNullOrWhiteSpace(Email) || !Email!.Contains('@')) { DialogResult = DialogResult.None; MessageBox.Show(L.NewUserForm_007); }
        };
        buttons.Controls.AddRange([ok, cancel]);

        Controls.Add(body);
        Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;
    }
}
