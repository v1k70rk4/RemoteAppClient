using System.Drawing;
using MaterialSkin.Controls;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient;

/// <summary>Egy frissen létrehozott/visszaállított hozzáférés megjelenítése: felhasználónév +
/// ideiglenes jelszó, KIMÁSOLHATÓ (kijelölhető mezők + „Másolás" gomb), hogy továbbküldhető legyen.</summary>
public sealed class CredentialDialog : MaterialForm
{
    public CredentialDialog(string title, string username, string secret,
        string? secretLabel = null, string? infoText = null)
    {
        secretLabel ??= L.CredentialDialog_001;
        ThemeManager.Skin.AddFormToManage(this);
        Text = title;
        Sizable = false;
        Width = 460; Height = 340;
        StartPosition = FormStartPosition.CenterParent;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20, 16, 20, 8) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var user = new MaterialTextBox2 { Hint = L.CredentialDialog_002, Text = username, ReadOnly = true, Dock = DockStyle.Fill, Margin = new Padding(3, 6, 3, 6) };
        var pass = new MaterialTextBox2 { Hint = secretLabel, Text = secret, ReadOnly = true, Dock = DockStyle.Fill, Margin = new Padding(3, 6, 3, 6) };
        var copy = new MaterialButton { Text = L.CredentialDialog_003 + secretLabel.ToLowerInvariant() + ")", Dock = DockStyle.Fill, Margin = new Padding(3, 8, 3, 6), Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(L.Format(L.CredentialDialog_004, username, secretLabel, secret)); copy.Text = L.CredentialDialog_005; }
            catch { copy.Text = L.CredentialDialog_006; }
        };
        var info = new MaterialLabel
        {
            Text = infoText ?? L.CredentialDialog_007,
            Dock = DockStyle.Fill, AutoSize = false, Height = 64, Margin = new Padding(3, 10, 3, 6),
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
