namespace RemoteClient;

/// <summary>Új felhasználó adatai (a szerver ideiglenes jelszót generál hozzá).</summary>
public sealed class NewUserForm : Form
{
    private readonly TextBox _username = new();
    private readonly TextBox _email = new();
    private readonly ComboBox _role = new() { DropDownStyle = ComboBoxStyle.DropDownList };

    public string Username => _username.Text.Trim();
    public string? Email => string.IsNullOrWhiteSpace(_email.Text) ? null : _email.Text.Trim();
    public string Role => (string)_role.SelectedItem!;

    public NewUserForm()
    {
        Text = "Új felhasználó";
        Width = 400; Height = 230;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        Lbl("Felhasználónév:", 18); _username.SetBounds(140, 14, 220, 24);
        Lbl("E-mail (opc.):", 52); _email.SetBounds(140, 48, 220, 24);
        Lbl("Szerep:", 86); _role.SetBounds(140, 82, 160, 24);
        _role.Items.AddRange(["operator", "admin"]); _role.SelectedIndex = 0;

        var ok = new Button { Text = "Létrehozás", DialogResult = DialogResult.OK, Bounds = new Rectangle(160, 130, 110, 30) };
        var cancel = new Button { Text = "Mégse", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(278, 130, 84, 30) };
        ok.Click += (_, _) => { if (Username.Length == 0) { DialogResult = DialogResult.None; MessageBox.Show("Adj meg felhasználónevet."); } };
        Controls.AddRange([_username, _email, _role, ok, cancel]);
        AcceptButton = ok; CancelButton = cancel;
    }

    private void Lbl(string t, int y) => Controls.Add(new Label { Text = t, Bounds = new Rectangle(16, y + 3, 120, 22) });
}
