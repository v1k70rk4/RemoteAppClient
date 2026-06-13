using System.Drawing;
using MaterialSkin.Controls;

namespace RemoteClient;

/// <summary>
/// Jelszó-emlékeztető: a user megadja a felhasználónevét + e-mail címét, kér egy kódot
/// (10 mp-enként egyszer), majd a kóddal új jelszót állít be. Az ablak nyitva marad a kód kéréséig.
/// Anti-enumeration: a „Kód kérése" mindig semleges üzenetet ad.
/// </summary>
public sealed class ForgotPasswordForm : MaterialForm
{
    private readonly AdminApi _api;

    private readonly MaterialTextBox2 _username = new() { Hint = "Felhasználónév" };
    private readonly MaterialTextBox2 _email = new() { Hint = "E-mail cím" };
    private readonly MaterialButton _requestBtn = new() { Text = "Token kérése", AutoSize = true };
    private readonly MaterialTextBox2 _token = new() { Hint = "Kapott token" };
    private readonly MaterialTextBox2 _newPass = new() { Hint = "Új jelszó (min. 10)", UseSystemPasswordChar = true };
    private readonly MaterialButton _setBtn = new() { Text = "Új jelszó beállítása", AutoSize = true };
    private readonly MaterialLabel _status = new() { AutoSize = true, MaximumSize = new Size(380, 0) };

    private readonly System.Windows.Forms.Timer _cooldown = new() { Interval = 1000 };
    private int _cooldownLeft;

    public ForgotPasswordForm(AdminApi api)
    {
        _api = api;
        ThemeManager.Skin.AddFormToManage(this);
        Text = "Jelszó helyreállítás";
        Sizable = false;
        Width = 440; Height = 600;
        StartPosition = FormStartPosition.CenterParent;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true, Padding = new Padding(20, 16, 20, 12) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(Control c, int top = 6) { c.Dock = DockStyle.Top; c.Margin = new Padding(3, top, 3, 6); body.RowStyles.Add(new RowStyle(SizeType.AutoSize)); body.Controls.Add(c); }

        Row(new MaterialLabel { Text = "1) Add meg a felhasználóneved és e-mailed, és kérj tokent.", AutoSize = true });
        Row(_username);
        Row(_email);
        Row(_requestBtn);
        Row(new MaterialLabel { Text = "2) Írd be a kapott tokent és az új jelszót.", AutoSize = true }, top: 14);
        Row(_token);
        Row(_newPass);
        Row(_setBtn);
        Row(_status, top: 10);

        _requestBtn.Click += async (_, _) => await RequestAsync();
        _setBtn.Click += async (_, _) => await SetAsync();
        _cooldown.Tick += (_, _) => Tick();

        Controls.Add(body);
    }

    private async Task RequestAsync()
    {
        var u = _username.Text.Trim();
        var e = _email.Text.Trim();
        if (u.Length == 0 || e.Length == 0) { _status.Text = "Add meg a felhasználónevet és az e-mailt."; return; }

        _requestBtn.Enabled = false;
        try { await _api.RequestPasswordCodeAsync(u, e); }
        catch { /* anti-enumeration: nem jelezzük */ }

        // Mindig semleges visszajelzés (nem áruljuk el, létezik-e a fiók).
        _status.Text = "Ha az adatok helyesek, elküldtük a tokent e-mailben (30 percig érvényes).";
        StartCooldown(10);
    }

    private async Task SetAsync()
    {
        var u = _username.Text.Trim();
        var code = _token.Text.Trim();
        var pw = _newPass.Text;
        if (u.Length == 0 || code.Length == 0) { _status.Text = "Add meg a felhasználónevet és a kapott tokent."; return; }
        if (pw.Length < 10) { _status.Text = "Az új jelszó legalább 10 karakter legyen."; return; }

        _setBtn.Enabled = false;
        try
        {
            var (ok, err) = await _api.ResetPasswordWithCodeAsync(u, code, pw);
            if (ok)
            {
                MessageBox.Show("A jelszó beállítva. Most már beléphetsz az új jelszóval.", "Kész", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            _status.Text = err switch
            {
                "invalid_code" => "Érvénytelen vagy lejárt token.",
                "weak_password" => "Az új jelszó legalább 10 karakter legyen.",
                "device_locked" => "Ez a gép a sok sikertelen próba miatt zárolt. Hívd a support-ot.",
                _ => "A jelszó beállítása nem sikerült.",
            };
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
        finally { _setBtn.Enabled = true; }
    }

    private void StartCooldown(int seconds)
    {
        _cooldownLeft = seconds;
        _requestBtn.Enabled = false;
        _requestBtn.Text = $"Token kérése ({_cooldownLeft})";
        _cooldown.Start();
    }

    private void Tick()
    {
        _cooldownLeft--;
        if (_cooldownLeft <= 0)
        {
            _cooldown.Stop();
            _requestBtn.Text = "Token kérése";
            _requestBtn.Enabled = true;
        }
        else _requestBtn.Text = $"Token kérése ({_cooldownLeft})";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _cooldown.Stop(); _cooldown.Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}
