using System.Drawing;
using MaterialSkin.Controls;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient;

/// <summary>
/// Jelszó-emlékeztető: a user megadja a felhasználónevét + e-mail címét, kér egy kódot
/// (10 mp-enként egyszer), majd a kóddal új jelszót állít be. Az ablak nyitva marad a kód kéréséig.
/// Anti-enumeration: a „Kód kérése" mindig semleges üzenetet ad.
/// </summary>
public sealed class ForgotPasswordForm : MaterialForm
{
    private readonly AdminApi _api;

    private readonly MaterialTextBox2 _username = new() { Hint = L.ForgotPasswordForm_001 };
    private readonly MaterialTextBox2 _email = new() { Hint = L.ForgotPasswordForm_002 };
    private readonly MaterialButton _requestBtn = new() { Text = L.ForgotPasswordForm_003, AutoSize = true };
    private readonly MaterialTextBox2 _token = new() { Hint = "Kapott token" };
    private readonly MaterialTextBox2 _newPass = new() { Hint = L.ForgotPasswordForm_004, UseSystemPasswordChar = true };
    private readonly MaterialButton _setBtn = new() { Text = L.ForgotPasswordForm_005, AutoSize = true };
    private readonly MaterialLabel _status = new() { AutoSize = true, MaximumSize = new Size(380, 0) };

    private readonly System.Windows.Forms.Timer _cooldown = new() { Interval = 1000 };
    private int _cooldownLeft;

    public ForgotPasswordForm(AdminApi api)
    {
        _api = api;
        ThemeManager.Skin.AddFormToManage(this);
        Text = L.ForgotPasswordForm_006;
        Sizable = false;
        Width = 440; Height = 600;
        StartPosition = FormStartPosition.CenterParent;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true, Padding = new Padding(20, 16, 20, 12) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(Control c, int top = 6) { c.Dock = DockStyle.Top; c.Margin = new Padding(3, top, 3, 6); body.RowStyles.Add(new RowStyle(SizeType.AutoSize)); body.Controls.Add(c); }

        Row(new MaterialLabel { Text = L.ForgotPasswordForm_007, AutoSize = true });
        Row(_username);
        Row(_email);
        Row(_requestBtn);
        Row(new MaterialLabel { Text = L.ForgotPasswordForm_008, AutoSize = true }, top: 14);
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
        if (u.Length == 0 || e.Length == 0) { _status.Text = L.ForgotPasswordForm_009; return; }

        _requestBtn.Enabled = false;
        try { await _api.RequestPasswordCodeAsync(u, e); }
        catch { /* anti-enumeration: nem jelezzük */ }

        // Mindig semleges visszajelzés (nem áruljuk el, létezik-e a fiók).
        _status.Text = L.ForgotPasswordForm_010;
        StartCooldown(10);
    }

    private async Task SetAsync()
    {
        var u = _username.Text.Trim();
        var code = _token.Text.Trim();
        var pw = _newPass.Text;
        if (u.Length == 0 || code.Length == 0) { _status.Text = L.ForgotPasswordForm_011; return; }
        if (pw.Length < 10) { _status.Text = L.ForgotPasswordForm_012; return; }

        _setBtn.Enabled = false;
        try
        {
            var (ok, err) = await _api.ResetPasswordWithCodeAsync(u, code, pw);
            if (ok)
            {
                MessageBox.Show(L.ForgotPasswordForm_013, L.ForgotPasswordForm_014, MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            _status.Text = err switch
            {
                "invalid_code" => L.ForgotPasswordForm_015,
                "weak_password" => L.ForgotPasswordForm_012,
                "device_locked" => L.ForgotPasswordForm_016,
                _ => L.ForgotPasswordForm_017,
            };
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_019 + ex.Message; }
        finally { _setBtn.Enabled = true; }
    }

    private void StartCooldown(int seconds)
    {
        _cooldownLeft = seconds;
        _requestBtn.Enabled = false;
        _requestBtn.Text = L.Format(L.ForgotPasswordForm_018, _cooldownLeft);
        _cooldown.Start();
    }

    private void Tick()
    {
        _cooldownLeft--;
        if (_cooldownLeft <= 0)
        {
            _cooldown.Stop();
            _requestBtn.Text = L.ForgotPasswordForm_003;
            _requestBtn.Enabled = true;
        }
        else _requestBtn.Text = L.Format(L.ForgotPasswordForm_018, _cooldownLeft);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _cooldown.Stop(); _cooldown.Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}
