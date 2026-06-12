using System.Drawing;
using System.IO;
using QRCoder;
using RemoteAgent.Admin;

namespace RemoteClient;

/// <summary>
/// Bejelentkezés + első belépéskor a kötelező beállítás: jelszócsere és/vagy TOTP-enroll
/// (QR-kód az authenticator apphoz). Siker után az <see cref="AdminApi"/>-n be van állítva
/// a session-token, és a DialogResult OK.
/// </summary>
public sealed class LoginForm : Form
{
    private readonly AdminApi _api;

    // 1. fázis — belépés
    private readonly TextBox _user = new();
    private readonly TextBox _pass = new() { UseSystemPasswordChar = true };
    private readonly TextBox _totp = new();
    private readonly Button _loginBtn = new() { Text = "Belépés" };

    // 2. fázis — beállítás (kezdetben rejtett)
    private readonly Label _setupHdr = new() { Text = "Első belépés — beállítás", Font = new Font(Control.DefaultFont, FontStyle.Bold) };
    private readonly Label _newPassLbl = new() { Text = "Új jelszó:" };
    private readonly TextBox _newPass = new() { UseSystemPasswordChar = true };
    private readonly Label _newPass2Lbl = new() { Text = "Új jelszó újra:" };
    private readonly TextBox _newPass2 = new() { UseSystemPasswordChar = true };
    private readonly Label _qrLbl = new() { Text = "Olvasd be az authenticator appal:" };
    private readonly PictureBox _qr = new() { SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Label _secretLbl = new() { Text = "Titok (kézi):" };
    private readonly TextBox _secret = new() { ReadOnly = true };
    private readonly Label _enrollLbl = new() { Text = "Hitelesítő kód:" };
    private readonly TextBox _enrollCode = new();
    private readonly Button _finishBtn = new() { Text = "Befejezés" };

    private readonly Label _status = new() { ForeColor = Color.Firebrick };

    private LoginResponse? _login;

    public LoginForm(AdminApi api)
    {
        _api = api;
        Text = "RemoteAppClient — bejelentkezés";
        Width = 440; Height = 235;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        Lbl("Felhasználó:", 16, 18); _user.SetBounds(140, 14, 260, 24);
        Lbl("Jelszó:", 16, 52); _pass.SetBounds(140, 48, 260, 24);
        Lbl("TOTP (ha van):", 16, 86); _totp.SetBounds(140, 82, 120, 24);
        _loginBtn.SetBounds(140, 120, 120, 32);
        _loginBtn.Click += async (_, _) => await DoLoginAsync();
        AcceptButton = _loginBtn;

        // 2. fázis
        _setupHdr.SetBounds(16, 168, 300, 22);
        _newPassLbl.SetBounds(16, 200, 120, 22); _newPass.SetBounds(140, 196, 200, 24);
        _newPass2Lbl.SetBounds(16, 232, 120, 22); _newPass2.SetBounds(140, 228, 200, 24);
        _qrLbl.SetBounds(16, 266, 360, 22);
        _qr.SetBounds(16, 292, 180, 180);
        _secretLbl.SetBounds(210, 300, 90, 22); _secret.SetBounds(210, 322, 200, 24);
        _enrollLbl.SetBounds(210, 360, 120, 22); _enrollCode.SetBounds(210, 382, 120, 24);
        _finishBtn.SetBounds(210, 430, 120, 32);
        _finishBtn.Click += async (_, _) => await DoFinishAsync();

        _status.SetBounds(16, 482, 400, 40);

        Controls.AddRange([
            _user, _pass, _totp, _loginBtn,
            _setupHdr, _newPassLbl, _newPass, _newPass2Lbl, _newPass2, _qrLbl, _qr, _secretLbl, _secret, _enrollLbl, _enrollCode, _finishBtn,
            _status,
        ]);
        SetSetupVisible(false);
    }

    private void Lbl(string text, int x, int y) =>
        Controls.Add(new Label { Text = text, Bounds = new Rectangle(x, y + 3, 120, 22) });

    private async Task DoLoginAsync()
    {
        _status.Text = "";
        try
        {
            _loginBtn.Enabled = false;
            _login = await _api.LoginAsync(_user.Text.Trim(), _pass.Text, string.IsNullOrWhiteSpace(_totp.Text) ? null : _totp.Text.Trim());
            _api.SetToken(_login.Token);

            if (!_login.MustChangePassword && !_login.TotpEnrollRequired)
            {
                DialogResult = DialogResult.OK; // kész
                return;
            }
            EnterSetup();
        }
        catch (AuthException ex)
        {
            _status.Text = ex.Code switch
            {
                "totp_required" => "Add meg a TOTP kódot.",
                "totp_invalid" => "Hibás TOTP kód.",
                "invalid_credentials" => "Hibás felhasználónév vagy jelszó.",
                _ => "Bejelentkezés sikertelen: " + ex.Code,
            };
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
        finally { _loginBtn.Enabled = true; }
    }

    private void EnterSetup()
    {
        // Jelszócsere mezők csak ha kell.
        bool pw = _login!.MustChangePassword;
        _newPassLbl.Visible = _newPass.Visible = _newPass2Lbl.Visible = _newPass2.Visible = pw;

        // TOTP-enroll: QR + titok + kód, ha kell.
        bool totp = _login.TotpEnrollRequired;
        _qrLbl.Visible = _qr.Visible = _secretLbl.Visible = _secret.Visible = _enrollLbl.Visible = _enrollCode.Visible = totp;
        if (totp && !string.IsNullOrWhiteSpace(_login.TotpUri))
        {
            _secret.Text = _login.TotpSecret ?? "";
            RenderQr(_login.TotpUri!);
        }

        _user.Enabled = _pass.Enabled = _totp.Enabled = _loginBtn.Enabled = false;
        SetSetupVisible(true);
        Height = totp ? 575 : 320;
        _status.SetBounds(16, totp ? 482 : 268, 400, 40);
        _status.Text = "";
    }

    private void SetSetupVisible(bool on)
    {
        _setupHdr.Visible = on;
        _finishBtn.Visible = on;
        if (!on)
        {
            foreach (var c in new Control[] { _newPassLbl, _newPass, _newPass2Lbl, _newPass2, _qrLbl, _qr, _secretLbl, _secret, _enrollLbl, _enrollCode })
                c.Visible = false;
        }
    }

    private void RenderQr(string uri)
    {
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(6);
            using var ms = new MemoryStream(png);
            _qr.Image?.Dispose();
            _qr.Image = new Bitmap(ms);
        }
        catch { /* a titok kézzel is beírható */ }
    }

    private async Task DoFinishAsync()
    {
        _status.Text = "";
        try
        {
            _finishBtn.Enabled = false;

            if (_login!.MustChangePassword)
            {
                if (_newPass.Text.Length < 10) { _status.Text = "A jelszó legyen legalább 10 karakter."; return; }
                if (_newPass.Text != _newPass2.Text) { _status.Text = "A két jelszó nem egyezik."; return; }
                await _api.ChangePasswordAsync(_newPass.Text);
            }

            if (_login.TotpEnrollRequired)
            {
                if (string.IsNullOrWhiteSpace(_enrollCode.Text)) { _status.Text = "Add meg a hitelesítő kódot."; return; }
                await _api.ConfirmTotpAsync(_enrollCode.Text.Trim());
            }

            DialogResult = DialogResult.OK; // a token már be van állítva, a flagek tisztultak
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
        finally { _finishBtn.Enabled = true; }
    }
}
