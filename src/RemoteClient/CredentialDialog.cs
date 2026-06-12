namespace RemoteClient;

/// <summary>Egy frissen létrehozott/visszaállított hozzáférés megjelenítése: felhasználónév +
/// ideiglenes jelszó, KIMÁSOLHATÓ (kijelölhető mezők + „Másolás" gomb), hogy továbbküldhető legyen.</summary>
public sealed class CredentialDialog : Form
{
    public CredentialDialog(string title, string username, string password)
    {
        Text = title;
        Width = 440; Height = 230;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        Lbl("Felhasználó:", 18);
        var user = new TextBox { ReadOnly = true, Text = username, Bounds = new Rectangle(150, 14, 250, 24) };

        Lbl("Ideiglenes jelszó:", 52);
        var pass = new TextBox { ReadOnly = true, Text = password, Bounds = new Rectangle(150, 48, 250, 24) };

        var copy = new Button { Text = "Másolás (user + jelszó)", Bounds = new Rectangle(150, 86, 250, 30) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText($"Felhasználó: {username}\nIdeiglenes jelszó: {password}"); copy.Text = "Vágólapra másolva ✓"; }
            catch { copy.Text = "A vágólap most foglalt — próbáld újra"; }
        };

        var info = new Label
        {
            Text = "Az első belépéskor jelszót cserél és TOTP-t (QR) állít be.",
            Bounds = new Rectangle(16, 128, 400, 36), ForeColor = SystemColors.GrayText,
        };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Bounds = new Rectangle(312, 162, 88, 30) };
        Controls.AddRange([user, pass, copy, info, ok]);
        AcceptButton = ok;
    }

    private void Lbl(string t, int y) => Controls.Add(new Label { Text = t, Bounds = new Rectangle(16, y + 3, 130, 22) });
}
