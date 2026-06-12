using System.Drawing;
using MaterialSkin.Controls;

namespace RemoteClient;

/// <summary>Egy frissen létrehozott/visszaállított hozzáférés megjelenítése: felhasználónév +
/// ideiglenes jelszó, KIMÁSOLHATÓ (kijelölhető mezők + „Másolás" gomb), hogy továbbküldhető legyen.</summary>
public sealed class CredentialDialog : MaterialForm
{
    public CredentialDialog(string title, string username, string password)
    {
        ThemeManager.Skin.AddFormToManage(this);
        Text = title;
        Sizable = false;
        Width = 460; Height = 320;
        StartPosition = FormStartPosition.CenterParent;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20, 16, 20, 8) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var user = new MaterialTextBox2 { Hint = "Felhasználó", Text = username, ReadOnly = true, Dock = DockStyle.Fill, Margin = new Padding(3, 6, 3, 6) };
        var pass = new MaterialTextBox2 { Hint = "Ideiglenes jelszó", Text = password, ReadOnly = true, Dock = DockStyle.Fill, Margin = new Padding(3, 6, 3, 6) };
        var copy = new MaterialButton { Text = "Másolás (user + jelszó)", Dock = DockStyle.Fill, Margin = new Padding(3, 8, 3, 6), Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText($"Felhasználó: {username}\nIdeiglenes jelszó: {password}"); copy.Text = "Vágólapra másolva ✓"; }
            catch { copy.Text = "A vágólap most foglalt — próbáld újra"; }
        };
        var info = new MaterialLabel
        {
            Text = "Az első belépéskor jelszót cserél és TOTP-t (QR) állít be.",
            Dock = DockStyle.Fill, AutoSize = false, Height = 44, Margin = new Padding(3, 10, 3, 6),
        };
        foreach (var c in new Control[] { user, pass, copy, info })
        {
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.Controls.Add(c);
        }

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 56, Padding = new Padding(0, 8, 16, 8) };
        var ok = new MaterialButton { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.Add(ok);

        Controls.Add(body);
        Controls.Add(buttons);
        AcceptButton = ok;
    }
}
