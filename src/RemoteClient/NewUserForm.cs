using System.Drawing;
using MaterialSkin.Controls;

namespace RemoteClient;

/// <summary>Új felhasználó adatai (a szerver ideiglenes jelszót generál hozzá).</summary>
public sealed class NewUserForm : MaterialForm
{
    private readonly MaterialTextBox2 _username = new() { Hint = "Felhasználónév" };
    private readonly MaterialTextBox2 _name = new() { Hint = "Megjelenítendő név (pl. Révész Viktor)" };
    private readonly MaterialTextBox2 _email = new() { Hint = "E-mail (opcionális)" };
    private readonly MaterialComboBox _role = new() { Hint = "Szerep" };

    public string Username => _username.Text.Trim();
    public string? Name => string.IsNullOrWhiteSpace(_name.Text) ? null : _name.Text.Trim();
    public string? Email => string.IsNullOrWhiteSpace(_email.Text) ? null : _email.Text.Trim();
    public string Role => (string)_role.SelectedItem!;

    public NewUserForm()
    {
        ThemeManager.Skin.AddFormToManage(this);
        Text = "Új felhasználó";
        Sizable = false;
        Width = 420; Height = 380;
        StartPosition = FormStartPosition.CenterParent;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20, 16, 20, 8) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var c in new Control[] { _username, _name, _email, _role })
        {
            c.Dock = DockStyle.Top; c.Margin = new Padding(3, 6, 3, 6);
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.Controls.Add(c);
        }
        _role.Items.AddRange(["operator", "admin"]); _role.SelectedIndex = 0;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 56, Padding = new Padding(0, 8, 16, 8) };
        var ok = new MaterialButton { Text = "Létrehozás", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new MaterialButton { Text = "Mégse", DialogResult = DialogResult.Cancel, AutoSize = true, Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        ok.Click += (_, _) => { if (Username.Length == 0) { DialogResult = DialogResult.None; MessageBox.Show("Adj meg felhasználónevet."); } };
        buttons.Controls.AddRange([ok, cancel]);

        Controls.Add(body);
        Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;
    }
}
