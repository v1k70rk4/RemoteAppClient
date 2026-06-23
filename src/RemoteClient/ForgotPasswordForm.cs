using System.Drawing;
using MaterialSkin.Controls;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient;

/// <summary>
/// Password recovery: the user enters username and email, requests a code at most every
/// 10 seconds, then sets a new password with that code. The form stays open after requesting.
/// Anti-enumeration: "request code" always shows a neutral message.
/// </summary>
public sealed class ForgotPasswordForm : MaterialForm
{
    private readonly AdminApi _api;

    private readonly MaterialTextBox2 _username = new() { Hint = L.ForgotPasswordForm_Username };
    private readonly MaterialTextBox2 _email = new() { Hint = L.ForgotPasswordForm_EmailAddress };
    private readonly MaterialButton _requestBtn = new() { Text = L.ForgotPasswordForm_RequestToken, AutoSize = true };
    private readonly MaterialTextBox2 _token = new() { Hint = "Kapott token" };
    private readonly MaterialTextBox2 _newPass = new() { Hint = L.ForgotPasswordForm_NewPasswordMin10, UseSystemPasswordChar = true };
    private readonly MaterialButton _setBtn = new() { Text = L.ForgotPasswordForm_SetNewPassword, AutoSize = true };
    private readonly MaterialLabel _status = new() { AutoSize = true, MaximumSize = new Size(380, 0) };

    private readonly System.Windows.Forms.Timer _cooldown = new() { Interval = 1000 };
    private int _cooldownLeft;

    public ForgotPasswordForm(AdminApi api)
    {
        _api = api;
        // Not AddFormToManage: it re-themes (greys) the redesigned main window. Material controls self-theme.
        BackColor = ThemeManager.Background;
        Text = L.ForgotPasswordForm_PasswordRecovery;
        Sizable = false;
        Width = 440; Height = 600;
        StartPosition = FormStartPosition.CenterParent;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true, Padding = new Padding(20, 16, 20, 12) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(Control c, int top = 6) { c.Dock = DockStyle.Top; c.Margin = new Padding(3, top, 3, 6); body.RowStyles.Add(new RowStyle(SizeType.AutoSize)); body.Controls.Add(c); }

        Row(new MaterialLabel { Text = L.ForgotPasswordForm_X1EnterYourUsernameAnd, AutoSize = true });
        Row(_username);
        Row(_email);
        Row(_requestBtn);
        Row(new MaterialLabel { Text = L.ForgotPasswordForm_X2EnterTheTokenYou, AutoSize = true }, top: 14);
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
        if (u.Length == 0 || e.Length == 0) { _status.Text = L.ForgotPasswordForm_EnterTheUsernameAndEmail; return; }

        _requestBtn.Enabled = false;
        try { await _api.RequestPasswordCodeAsync(u, e); }
        catch { /* anti-enumeration: do not reveal details */ }

        // Always neutral feedback; do not reveal whether the account exists.
        _status.Text = L.ForgotPasswordForm_IfTheDetailsAreCorrect;
        StartCooldown(10);
    }

    private async Task SetAsync()
    {
        var u = _username.Text.Trim();
        var code = _token.Text.Trim();
        var pw = _newPass.Text;
        if (u.Length == 0 || code.Length == 0) { _status.Text = L.ForgotPasswordForm_EnterTheUsernameAndThe; return; }
        if (pw.Length < 10) { _status.Text = L.ForgotPasswordForm_TheNewPasswordMustBe; return; }

        _setBtn.Enabled = false;
        try
        {
            var (ok, err) = await _api.ResetPasswordWithCodeAsync(u, code, pw);
            if (ok)
            {
                MessageBox.Show(L.ForgotPasswordForm_PasswordSetYouCanNow, L.ForgotPasswordForm_Done, MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            _status.Text = err switch
            {
                "invalid_code" => L.ForgotPasswordForm_InvalidOrExpiredToken,
                "weak_password" => L.ForgotPasswordForm_TheNewPasswordMustBe,
                "device_locked" => L.ForgotPasswordForm_ThisDeviceIsLockedDue,
                _ => L.ForgotPasswordForm_PasswordSetupFailed,
            };
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
        finally { _setBtn.Enabled = true; }
    }

    private void StartCooldown(int seconds)
    {
        _cooldownLeft = seconds;
        _requestBtn.Enabled = false;
        _requestBtn.Text = L.Format(L.ForgotPasswordForm_RequestToken_2, _cooldownLeft);
        _cooldown.Start();
    }

    private void Tick()
    {
        _cooldownLeft--;
        if (_cooldownLeft <= 0)
        {
            _cooldown.Stop();
            _requestBtn.Text = L.ForgotPasswordForm_RequestToken;
            _requestBtn.Enabled = true;
        }
        else _requestBtn.Text = L.Format(L.ForgotPasswordForm_RequestToken_2, _cooldownLeft);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _cooldown.Stop(); _cooldown.Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}
