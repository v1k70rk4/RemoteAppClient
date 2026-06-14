using System.Diagnostics;
using System.Drawing;
using System.IO;
using MaterialSkin.Controls;
using QRCoder;
using RemoteAgent.Admin;
using RemoteClient.Views;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient;

/// <summary>
/// Main console window (MaterialSkin). Three states: (1) no local agent, (2) agent present
/// with server status + sign-in, (3) signed in with devices + admin features. Transport is
/// provided by the local agent broker using the device SSH key; the console only works on enrolled devices.
/// </summary>
public sealed class MainForm : MaterialForm
{
    private readonly ClientConfig _cfg;
    private BrokerClient? _broker;
    private AdminApi? _api;
    private string _role = "operator";
    private string _username = "";
    private bool _started;
    private LoginResponse? _login;

    // Views
    private readonly Panel _noAgentView = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel _authView = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel _mainView = new() { Dock = DockStyle.Fill, Visible = false };

    // Auth view controls
    private readonly MaterialLabel _serverNameLbl = new();
    private readonly MaterialLabel _onlineLbl = new();
    private readonly MaterialLabel _remoteLbl = new();
    private readonly MaterialLabel _supportLbl = new();        // owner + support in login header
    private readonly MaterialLabel _noAgentSupportLbl = new(); // support on the "no agent" screen
    private RemoteAgent.Admin.BrandingInfo? _branding;
    private readonly MaterialCard _loginCard = new();
    private readonly MaterialCard _setupCard = new();
    private readonly MaterialTextBox2 _user = new() { Hint = L.CredentialDialog_User };
    private readonly MaterialTextBox2 _pass = new() { Hint = L.MainForm_Password, UseSystemPasswordChar = true };
    private readonly MaterialTextBox2 _totp = new() { Hint = L.MainForm_TOTPIfAny };
    private readonly MaterialButton _loginBtn = new() { Text = L.MainForm_SignIn };
    private readonly MaterialButton _helloBtn = new() { Text = L.MainForm_SignInWithWindowsHello, Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false, Visible = false };
    private readonly MaterialLabel _loginStatus = new() { Visible = true };
    private readonly MaterialLabel _forgotLink = new() { Text = L.MainForm_ForgotPassword, AutoSize = true, ForeColor = Color.DodgerBlue, Cursor = Cursors.Hand };
    private bool _loggedInViaHello;
    // Setup
    private readonly MaterialTextBox2 _newPass = new() { Hint = L.ForgotPasswordForm_NewPasswordMin10, UseSystemPasswordChar = true };
    private readonly MaterialTextBox2 _newPass2 = new() { Hint = L.MainForm_RepeatNewPassword, UseSystemPasswordChar = true };
    private readonly PictureBox _qr = new() { SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(160, 160) };
    private readonly MaterialTextBox2 _enrollCode = new() { Hint = L.MainForm_AuthenticatorCode };
    private readonly MaterialButton _finishBtn = new() { Text = L.MainForm_Finish };
    private readonly MaterialLabel _setupStatus = new();

    // Main view: left menu plus right content host in one window.
    private readonly MaterialLabel _envLbl = new();   // live environment indicator from local agent status pipe
    private readonly System.Windows.Forms.Timer _envTimer = new() { Interval = 3000 };
    private readonly MaterialLabel _verLbl = new();   // ver: x.y.z in the lower-left corner
    private readonly Panel _warnPanel = new() { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false };
    private readonly MaterialLabel _secretWarnLbl = new(); // secret expiry warning above online status
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _nav = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8, 8, 8, 8) };
    private readonly List<(MaterialButton Btn, IContentView View)> _navItems = new();
    private IContentView? _currentView;

    // View instances are created after sign-in, once _api/_broker exist.
    private DevicesView? _devicesView;
    private UsersView? _usersView;
    private GroupsView? _groupsView;
    private ChannelsView? _channelsView;
    private BootstrapView? _bootstrapView;
    private SettingsView? _settingsView;
    private AboutView? _aboutView;
    private LogView? _logView;
    private ServerSettingsView? _serverSettingsView;

    public MainForm()
    {
        _cfg = ClientConfig.Load();
        ThemeManager.Skin.AddFormToManage(this);
        ThemeManager.Init(ThemeManager.ResolveDark(_cfg.ThemeMode));

        Text = "RemoteAppClient";
        try { if (Environment.ProcessPath is { } exe) Icon = Icon.ExtractAssociatedIcon(exe); } catch { /* icon is optional */ }
        Width = 1040; Height = 640;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 560);

        BuildNoAgentView();
        BuildAuthView();
        BuildMainView();
        Controls.AddRange([_mainView, _authView, _noAgentView]);

        Shown += async (_, _) => { if (!_started) { _started = true; await InitAsync(); } };
        FormClosing += OnFormClosing;
    }

    private bool _cleaned;

    // On exit, sign out and close the tunnel in the background first so the UI does not freeze.
    // A small "exit in progress" window is shown during cleanup.
    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_cleaned) return;            // background cleanup is done; allow close
        e.Cancel = true;                 // clean up first, then close

        try { _envTimer.Stop(); } catch { /* best effort */ }

        using var overlay = new ShutdownOverlay(ThemeManager.ResolveDark(_cfg.ThemeMode));
        overlay.Show(this);
        overlay.Refresh();
        Enabled = false;

        await Task.Run(Cleanup);

        _cleaned = true;
        Close();
    }

    // ---------------- State Transitions ----------------

    private void Show(Panel view)
    {
        foreach (var v in new[] { _noAgentView, _authView, _mainView }) v.Visible = v == view;
        view.BringToFront();
    }

    /// <summary>
    /// Opens a fresh admin API forward through the local broker. If the named pipe died,
    /// for example after sleep, it is discarded, reconnected, and retried. AdminApi calls
    /// this from ConnectCallback, so dead tunnels rebuild themselves instead of surfacing errors.
    /// </summary>
    private async Task<int> RefreshAdminForwardAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                _broker ??= await BrokerClient.TryConnectAsync()
                    ?? throw new InvalidOperationException(L.MainForm_NoLocalAgentBrokerUnavailable);
                return await _broker.ForwardAsync(_cfg.AdminApiPort, ct);
            }
            catch when (attempt == 0)
            {
                // Named pipe may have died during sleep; discard and reconnect.
                try { _broker?.Dispose(); } catch { /* best effort */ }
                _broker = null;
            }
        }
        throw new InvalidOperationException(L.MainForm_CouldNotOpenTheAdmin);
    }

    private bool _envBusy;

    /// <summary>Queries the local agent status pipe and updates the live environment indicator.</summary>
    private async Task RefreshEnvAsync()
    {
        if (_envBusy) return;
        _envBusy = true;
        try
        {
            var s = await StatusClient.QueryAgentAsync();
            if (_api is not null && !string.IsNullOrWhiteSpace(s?.DeviceId)) _api.DeviceId = s!.DeviceId;
            string text; Color color;
            if (s is null) { text = L.MainForm_AgentUnavailable; color = Color.Gray; }
            else if (!s.C2Connected) { text = L.MainForm_ServerNoConnection; color = Color.IndianRed; }
            else { text = s.TunnelActive ? L.MainForm_OnlineTunnelReady : "● Online"; color = Color.MediumSeaGreen; }
            if (!_envLbl.IsDisposed) { _envLbl.Text = text; _envLbl.ForeColor = color; }
        }
        catch { /* indicator is non-critical */ }
        finally { _envBusy = false; }
    }

    private void ApplyBranding()
    {
        // Branding appears in the blue title bar (Form.Text), e.g. "Coimbra ITS RemoteAppClient".
        Text = string.IsNullOrWhiteSpace(_branding?.OwnerName)
            ? "RemoteAppClient"
            : $"{_branding!.OwnerName} RemoteAppClient";

        var support = SupportLine();
        if (!_supportLbl.IsDisposed) _supportLbl.Text = support;
        if (!_noAgentSupportLbl.IsDisposed) _noAgentSupportLbl.Text = support;
    }

    private string SupportLine()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_branding?.SupportPhone)) parts.Add("☎ " + _branding!.SupportPhone);
        if (!string.IsNullOrWhiteSpace(_branding?.SupportEmail)) parts.Add("✉ " + _branding!.SupportEmail);
        return parts.Count == 0 ? "" : L.MainForm_Support + string.Join("    ", parts);
    }

    /// <summary>Admin: whether Graph secret expires within 30 days; shown as red warning above online status.</summary>
    private async Task CheckSecretExpiryAsync()
    {
        try
        {
            var s = await _api!.GetSettingsAsync();
            if (s.EmailProvider == "graph" && s.GraphSecretExpiresAt is { } exp)
            {
                var days = (exp - DateTimeOffset.UtcNow).TotalDays;
                if (days <= 30)
                {
                    _secretWarnLbl.Text = days <= 0
                        ? L.MainForm_TheEmailDeliveryTokenHas
                        : L.Format(L.MainForm_TheEmailDeliveryTokenExpires, (int)days);
                    _warnPanel.Visible = true;
                    return;
                }
            }
            _warnPanel.Visible = false;
        }
        catch { /* non-critical */ }
    }

    /// <summary>Refreshes branding from the server through the tunnel and caches it. Quietly fault-tolerant.</summary>
    private async Task RefreshBrandingAsync()
    {
        if (_api is null) return;
        var b = await _api.GetBrandingAsync();
        if (b is null) return;
        _branding = b;
        BrandingCache.Save(b);
        ApplyBranding();
    }

    private async Task InitAsync()
    {
        // Cached branding immediately, visible even before sign-in/agent.
        _branding = BrandingCache.Load();
        ApplyBranding();

        SetLoginStatus(L.MainForm_LookingForLocalAgent);
        _broker = await BrokerClient.TryConnectAsync();
        if (_broker is null) { Show(_noAgentView); return; }

        // Status: server name, online state, and local VNC lock.
        _serverNameLbl.Text = L.MainForm_Server + AgentInfo.ServerName();
        _remoteLbl.Text = L.MainForm_RemoteAccessOnThisDevice + (LocalVncLock.IsLocked() ? L.MainForm_DISABLED : L.MainForm_Enabled);
        Show(_authView);

        // Live environment indicator: poll local agent status pipe for realtime C2/tunnel.
        _envTimer.Tick += async (_, _) => await RefreshEnvAsync();
        _envTimer.Start();
        _ = RefreshEnvAsync();

        try
        {
            _api = new AdminApi(RefreshAdminForwardAsync);

            // Broker ssh -L may become usable a few seconds after reserving the port due to
            // cold SSH handshake. Avoid false "not responding" warnings by pinging for about 15s.
            _onlineLbl.Text = L.MainForm_Connecting; _onlineLbl.ForeColor = Color.Goldenrod;
            SetLoginStatus(L.MainForm_ConnectingToTheServer);
            bool online = false;
            for (int i = 0; i < 15 && !online; i++)
            {
                online = await _api.PingAsync();
                if (!online) await Task.Delay(1000);
            }
            _onlineLbl.Text = online ? "● Online" : "● Offline";
            _onlineLbl.ForeColor = online ? Color.MediumSeaGreen : Color.IndianRed;
            SetLoginStatus(online ? "" : L.MainForm_TheServerIsNotResponding);

            if (online) await RefreshBrandingAsync(); // fresh branding through tunnel, even before login
        }
        catch (Exception ex)
        {
            _onlineLbl.Text = "● Offline";
            _onlineLbl.ForeColor = Color.IndianRed;
            SetLoginStatus(L.MainForm_ChannelError + ex.Message);
        }

        // Windows Hello button only when this device has credentialId and Hello is available.
        try
        {
            _helloBtn.Visible = _cfg.HelloCredentialId is not null
                && !string.IsNullOrWhiteSpace(_cfg.HelloUsername)
                && await WindowsHello.IsAvailableAsync()
                && await WindowsHello.ExistsAsync(HelloKeyName(_cfg.HelloUsername!));
        }
        catch { _helloBtn.Visible = false; }
    }

    private void OpenForgotPassword()
    {
        if (_api is null) { SetLoginStatus(L.MainForm_NoConnectionToTheServer); return; }
        using var f = new ForgotPasswordForm(_api);
        f.ShowDialog(this);
    }

    private static string HelloKeyName(string username) => "RemoteAppClient-" + username;

    // ---------------- Build Views ----------------

    private void BuildNoAgentView()
    {
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var card = new MaterialCard { Width = 470, Height = 250, Anchor = AnchorStyles.None };
        var icon = new MaterialLabel { Text = "⚠", Font = new Font("Segoe UI", 36F), AutoSize = true, Location = new Point(24, 18) };
        var title = new MaterialLabel { Text = L.MainForm_NoLocalAgent, Font = new Font("Segoe UI", 15F, FontStyle.Bold), AutoSize = true, Location = new Point(90, 28) };
        var body = new MaterialLabel
        {
            Text = L.MainForm_RemoteAgentServiceIsNotRunning,
            AutoSize = false, Location = new Point(28, 84), Size = new Size(414, 100),
        };
        _noAgentSupportLbl.AutoSize = false; _noAgentSupportLbl.Location = new Point(28, 192); _noAgentSupportLbl.Size = new Size(414, 48);
        _noAgentSupportLbl.FontType = MaterialSkin.MaterialSkinManager.fontType.Caption;
        card.Controls.AddRange([icon, title, body, _noAgentSupportLbl]);
        center.Controls.Add(card);
        _noAgentView.Controls.Add(center);
    }

    private void BuildAuthView()
    {
        // Status header.
        var header = new MaterialCard { Dock = DockStyle.Top, Height = 116, Padding = new Padding(20) };
        _serverNameLbl.Font = new Font("Segoe UI", 14F, FontStyle.Bold); _serverNameLbl.AutoSize = true; _serverNameLbl.Location = new Point(20, 14);
        _onlineLbl.Text = "● …"; _onlineLbl.AutoSize = true; _onlineLbl.Location = new Point(22, 50);
        _remoteLbl.AutoSize = true; _remoteLbl.Location = new Point(160, 50);
        _supportLbl.AutoSize = true; _supportLbl.Location = new Point(22, 78);
        _supportLbl.FontType = MaterialSkin.MaterialSkinManager.fontType.Caption;
        header.Controls.AddRange([_serverNameLbl, _onlineLbl, _remoteLbl, _supportLbl]);

        // Login + setup cards in one centered TableLayoutPanel cell.
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _loginCard.Anchor = AnchorStyles.None;
        _setupCard.Anchor = AnchorStyles.None;
        _loginCard.Size = new Size(360, 420);
        var lt = new MaterialLabel { Text = L.MainForm_SignIn_2, Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Location = new Point(20, 16) };
        _user.SetBounds(20, 56, 320, 48);
        _pass.SetBounds(20, 110, 320, 48);
        _totp.SetBounds(20, 164, 320, 48);
        _loginBtn.SetBounds(20, 222, 320, 40);
        _loginBtn.Click += async (_, _) => await DoLoginAsync();
        _helloBtn.SetBounds(20, 268, 320, 40);
        _helloBtn.Click += async (_, _) => await DoHelloLoginAsync();
        _forgotLink.Location = new Point(20, 316);
        _forgotLink.Click += (_, _) => OpenForgotPassword();
        _loginStatus.SetBounds(20, 344, 320, 60); _loginStatus.ForeColor = Color.IndianRed;
        _loginCard.Controls.AddRange([lt, _user, _pass, _totp, _loginBtn, _helloBtn, _forgotLink, _loginStatus]);
        AcceptButton = _loginBtn;

        // Setup card for first sign-in, hidden initially.
        _setupCard.Size = new Size(420, 470); _setupCard.Visible = false;
        var st = new MaterialLabel { Text = L.MainForm_FirstSignInSetup, Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Location = new Point(20, 16) };
        _newPass.SetBounds(20, 56, 380, 48);
        _newPass2.SetBounds(20, 110, 380, 48);
        var ql = new MaterialLabel { Text = L.MainForm_ScanWithAnAuthenticatorApp, AutoSize = true, Location = new Point(20, 168) };
        _qr.Location = new Point(20, 196);
        _enrollCode.SetBounds(200, 210, 200, 48);
        _finishBtn.SetBounds(200, 270, 200, 40);
        _finishBtn.Click += async (_, _) => await DoFinishAsync();
        _setupStatus.SetBounds(20, 380, 380, 60); _setupStatus.ForeColor = Color.IndianRed;
        _setupCard.Controls.AddRange([st, _newPass, _newPass2, ql, _qr, _enrollCode, _finishBtn, _setupStatus]);

        center.Controls.Add(_loginCard, 0, 0);
        center.Controls.Add(_setupCard, 0, 0); // same cell, overlapped; only one visible at a time
        _authView.Controls.AddRange([center, header]); // center fills, header docks top
    }

    private void BuildMainView()
    {
        // --- Left sidebar: menu plus online/version footer. Branding stays in the blue title bar. ---
        var sidebar = new MaterialCard { Dock = DockStyle.Left, Width = 220, Margin = new Padding(0), Padding = new Padding(0) };

        // Lower-left: online indicator + client version. Theme selector moved to Settings.
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 56 };
        _envLbl.AutoSize = true; _envLbl.MaximumSize = new Size(200, 0);
        _envLbl.Location = new Point(12, 8); _envLbl.Text = L.MainForm_Environment; _envLbl.ForeColor = Color.Gray;
        _verLbl.AutoSize = true; _verLbl.Location = new Point(12, 32);
        _verLbl.Text = "ver: " + ClientUpdater.RunningVersionString();
        _verLbl.FontType = MaterialSkin.MaterialSkinManager.fontType.Caption;
        footer.Controls.AddRange([_envLbl, _verLbl]);

        // Above online: secret-expiry warning, red and visible only when relevant.
        _secretWarnLbl.AutoSize = true; _secretWarnLbl.MaximumSize = new Size(200, 0);
        _secretWarnLbl.ForeColor = Color.IndianRed; _secretWarnLbl.Margin = new Padding(12, 6, 8, 6);
        _secretWarnLbl.FontType = MaterialSkin.MaterialSkinManager.fontType.Caption;
        _warnPanel.Padding = new Padding(0, 2, 0, 2);
        _warnPanel.Controls.Add(_secretWarnLbl);

        sidebar.Controls.Add(_nav);        // Fill
        sidebar.Controls.Add(_warnPanel);  // Bottom; added first so it sits above footer
        sidebar.Controls.Add(footer);      // Bottom; added last so it sits at the very bottom

        _mainView.Controls.Add(_content); // Fill (jobb oldal)
        _mainView.Controls.Add(sidebar);  // Left
    }

    /// <summary>Creates a sidebar menu button and the associated view.</summary>
    private void AddNav(string text, IContentView view)
    {
        var b = new MaterialButton
        {
            Text = text, AutoSize = false, Width = 200, Height = 44,
            Type = MaterialButton.MaterialButtonType.Text, HighEmphasis = false,
            Margin = new Padding(0, 0, 0, 4),
        };
        b.Click += async (_, _) => await SwitchToAsync(view);
        _nav.Controls.Add(b);
        _navItems.Add((b, view));
    }

    private async Task SwitchToAsync(IContentView view)
    {
        if (ReferenceEquals(_currentView, view)) return;
        _currentView = view;

        // Highlight active menu item.
        foreach (var (btn, v) in _navItems)
        {
            bool active = ReferenceEquals(v, view);
            btn.Type = active ? MaterialButton.MaterialButtonType.Contained : MaterialButton.MaterialButtonType.Text;
            btn.HighEmphasis = active;
        }

        var ctl = (Control)view;
        ctl.Dock = DockStyle.Fill;
        _content.Controls.Clear();
        _content.Controls.Add(ctl);
        view.ApplyTheme();
        await view.OnShownAsync();
    }

    /// <summary>Applies theme mode ("light"/"dark"/"auto"), saves it, resolves it, and applies everywhere.</summary>
    private void ApplyThemeMode(string mode)
    {
        _cfg.ThemeMode = mode; try { _cfg.Save(); } catch { }
        ThemeManager.SetDark(ThemeManager.ResolveDark(mode));
        _content.BackColor = ThemeManager.Background;
        foreach (var (_, v) in _navItems) v.ApplyTheme();
        Invalidate(true);
    }

    // ---------------- Login + setup ----------------

    /// <summary>
    /// If the server mandates an update because the client is too old, downloads, replaces,
    /// and restarts. true = handled; caller must not continue.
    /// </summary>
    private async Task<bool> HandleMandatoryUpdateAsync(LoginResponse login)
    {
        if (!login.MustUpdate) return false;
        SetLoginStatus(L.MainForm_DownloadingRequiredUpdate);
        if (!string.IsNullOrWhiteSpace(login.UpdateFileName)
            && await ClientUpdater.ApplyKnownAsync(_api!, login.UpdateFileName!, login.UpdateSha256))
        {
            Cleanup();
            _cleaned = true;
            Application.Exit();
            return true;
        }
        MessageBox.Show(
            L.MainForm_ThisClientIsOutdatedAnd,
            L.MainForm_UpdateRequired, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        SetLoginStatus(L.MainForm_OutdatedClientUpdateRequired);
        return true;
    }

    private async Task DoLoginAsync()
    {
        SetLoginStatus("");
        if (_api is null) { SetLoginStatus(L.MainForm_NoConnectionToTheServer_2); return; }
        try
        {
            _loginBtn.Enabled = false;
            _username = _user.Text.Trim();
            _login = await _api.LoginAsync(_username, _pass.Text, string.IsNullOrWhiteSpace(_totp.Text) ? null : _totp.Text.Trim(),
                ClientUpdater.RunningVersionString(), _cfg.Channel);
            if (await HandleMandatoryUpdateAsync(_login)) return;
            _api.SetToken(_login.Token);
            _role = _login.Role;

            if (_login.MustChangePassword || _login.TotpEnrollRequired) { EnterSetup(); return; }
            await EnterMainAsync();
        }
        catch (AuthException ex)
        {
            SetLoginStatus(ex.Code switch
            {
                "totp_required" => L.MainForm_EnterTheTOTPCode,
                "totp_invalid" => L.MainForm_InvalidTOTPCode,
                "invalid_credentials" => L.MainForm_InvalidUsernameOrPassword,
                "device_locked" => L.MainForm_ThisDeviceIsSignIn,
                _ => L.MainForm_SignInFailed + ex.Code,
            });
        }
        catch (Exception ex) { SetLoginStatus(L.ForgotPasswordForm_Error + ex.Message); }
        finally { _loginBtn.Enabled = true; }
    }

    private async Task DoHelloLoginAsync()
    {
        SetLoginStatus("");
        if (_api is null) { SetLoginStatus(L.MainForm_NoConnectionToTheServer_2); return; }
        if (_cfg.HelloCredentialId is not { } credId || string.IsNullOrWhiteSpace(_cfg.HelloUsername))
        { SetLoginStatus(L.MainForm_WindowsHelloIsNotSet); return; }
        var user = _cfg.HelloUsername!;
        try
        {
            _helloBtn.Enabled = false;
            SetLoginStatus("Windows Hello…");
            var challenge = await _api.HelloChallengeAsync(user);
            var sig = await WindowsHello.SignAsync(HelloKeyName(user), challenge);
            if (sig is null) { SetLoginStatus(L.MainForm_WindowsHelloWasCancelledOr); return; }
            _login = await _api.HelloLoginAsync(user, credId, sig, ClientUpdater.RunningVersionString(), _cfg.Channel);
            if (await HandleMandatoryUpdateAsync(_login)) return;
            _api.SetToken(_login.Token);
            _role = _login.Role;
            _username = user;
            _loggedInViaHello = true;
            if (_login.MustChangePassword || _login.TotpEnrollRequired) { EnterSetup(); return; }
            await EnterMainAsync();
        }
        catch (AuthException ex)
        {
            SetLoginStatus(ex.Code switch
            {
                "challenge_expired" => L.MainForm_HelloSignInExpiredTry,
                "hello_unknown" => L.MainForm_ThisHelloDeviceIsNo,
                "hello_invalid" => L.MainForm_TheHelloSignatureIsInvalid,
                "invalid_credentials" => L.MainForm_TheUserIsInactiveOr,
                _ => L.MainForm_HelloSignInFailed + ex.Code,
            });
            if (ex.Code == "hello_unknown")
            {
                _cfg.HelloCredentialId = null; _cfg.HelloUsername = null; try { _cfg.Save(); } catch { }
                _helloBtn.Visible = false;
            }
        }
        catch (Exception ex) { SetLoginStatus(L.ForgotPasswordForm_Error + ex.Message); }
        finally { _helloBtn.Enabled = true; }
    }

    /// <summary>Offers Windows Hello setup on this device after password sign-in, once.</summary>
    private async Task OfferHelloSetupAsync()
    {
        if (_api is null || _loggedInViaHello) return;
        if (_cfg.HelloCredentialId is not null) return;           // already configured
        if (!await WindowsHello.IsAvailableAsync()) return;        // no Hello on this device
        if (MessageBox.Show(
                L.MainForm_WouldYouLikeToSign +
                L.MainForm_ThePrivateKeyStaysIn,
                L.MainForm_SetUpWindowsHello, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        try
        {
            var pub = await WindowsHello.CreateAsync(HelloKeyName(_username));
            if (pub is null) return; // user canceled the Hello prompt
            var credId = await _api.RegisterHelloAsync(pub, Environment.MachineName);
            _cfg.HelloCredentialId = credId; _cfg.HelloUsername = _username; try { _cfg.Save(); } catch { }
            MessageBox.Show(L.MainForm_WindowsHelloIsSetUp,
                "Windows Hello", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.MainForm_WindowsHelloSetupError + ex.Message, "Windows Hello", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void EnterSetup()
    {
        _loginCard.Visible = false;
        bool pw = _login!.MustChangePassword;
        _newPass.Visible = _newPass2.Visible = pw;
        bool totp = _login.TotpEnrollRequired;
        _qr.Visible = _enrollCode.Visible = totp;
        if (totp && !string.IsNullOrWhiteSpace(_login.TotpUri)) RenderQr(_login.TotpUri!);
        _setupCard.Visible = true;
        _setupCard.BringToFront();
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
        catch { /* secret can be typed manually */ }
    }

    private async Task DoFinishAsync()
    {
        _setupStatus.Text = "";
        try
        {
            _finishBtn.Enabled = false;
            if (_login!.MustChangePassword)
            {
                if (_newPass.Text.Length < 10) { _setupStatus.Text = L.MainForm_PasswordMustBeAtLeast; return; }
                if (_newPass.Text != _newPass2.Text) { _setupStatus.Text = L.MainForm_TheTwoPasswordsDoNot; return; }
                await _api!.ChangePasswordAsync(_newPass.Text);
            }
            if (_login.TotpEnrollRequired)
            {
                if (string.IsNullOrWhiteSpace(_enrollCode.Text)) { _setupStatus.Text = L.MainForm_EnterTheAuthenticatorCode; return; }
                await _api!.ConfirmTotpAsync(_enrollCode.Text.Trim());
            }
            await EnterMainAsync();
        }
        catch (Exception ex) { _setupStatus.Text = L.ForgotPasswordForm_Error + ex.Message; }
        finally { _finishBtn.Enabled = true; }
    }

    /// <summary>
    /// Self-update channel = this device's fleet channel resolved by hostname, so
    /// agent/helper/client/vnc follow the same setting. If the device is not in the fleet
    /// or resolution fails, local config channel is the fallback. The resolved value is saved.
    /// </summary>
    private async Task<string> ResolveUpdateChannelAsync()
    {
        try
        {
            var me = Environment.MachineName;
            var devices = await _api!.GetDevicesAsync();
            var dev = devices.FirstOrDefault(d => string.Equals(d.Hostname, me, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(dev?.Channel))
            {
                var ch = dev!.Channel!.Trim().ToLowerInvariant();
                if (!string.Equals(_cfg.Channel, ch, StringComparison.Ordinal))
                {
                    _cfg.Channel = ch;
                    _cfg.Save();
                }
            }
        }
        catch { /* fallback a configra */ }
        return _cfg.Channel;
    }

    private async Task EnterMainAsync()
    {
        // Silent self-update: if a newer 'client' exists on the channel, replace and restart.
        // Channel is resolved from this device's fleet channel by hostname; fallback is local config.
        if (_api is not null)
        {
            SetLoginStatus(L.MainForm_CheckingForUpdates);
            var channel = await ResolveUpdateChannelAsync();
            if (await ClientUpdater.CheckAndUpdateAsync(_api, channel)) { Cleanup(); _cleaned = true; Application.Exit(); return; }
        }

        ApplyBranding(); // blue title bar branding

        // Create views + menu according to role; operators only see Devices.
        _devicesView = new DevicesView(_api!, _broker!, _cfg, _role == "admin");
        AddNav(L.MainForm_Devices, _devicesView);
        if (_role == "admin")
        {
            _usersView = new UsersView(_api!, _username);
            _groupsView = new GroupsView(_api!);
            _channelsView = new ChannelsView(_api!);
            _bootstrapView = new BootstrapView(_api!);
            _logView = new LogView(_api!);
            _serverSettingsView = new ServerSettingsView(_api!);
            AddNav(L.MainForm_Users, _usersView);
            AddNav(L.MainForm_Groups, _groupsView);
            AddNav(L.MainForm_ChannelsMSI, _channelsView);
            AddNav("Bootstrap", _bootstrapView);
            AddNav(L.MainForm_Log, _logView);
            AddNav(L.MainForm_ServerSettings, _serverSettingsView);
            _ = CheckSecretExpiryAsync();
        }

        // Settings + About are visible to everyone; local settings are not admin-dependent.
        _settingsView = new SettingsView(_cfg.ThemeMode, ApplyThemeMode, _role == "admin");
        _aboutView = new AboutView(_cfg);
        AddNav(L.MainForm_Settings, _settingsView);
        AddNav(L.MainForm_About, _aboutView);

        ApplyThemeMode(_cfg.ThemeMode);
        Show(_mainView);
        await SwitchToAsync(_devicesView);

        // After password sign-in, offer Windows Hello setup on this device when missing.
        await OfferHelloSetupAsync();
    }

    private void SetLoginStatus(string text) => _loginStatus.Text = text;

    private void Cleanup()
    {
        try { _envTimer.Stop(); _envTimer.Dispose(); } catch { /* best effort */ }
        try { _api?.LogoutAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
        _broker?.Dispose();
        _api?.Dispose();
    }
}

/// <summary>Small borderless "exit in progress" indicator shown during shutdown cleanup.</summary>
internal sealed class ShutdownOverlay : Form
{
    public ShutdownOverlay(bool dark)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        ControlBox = false;
        Size = new Size(280, 90);
        BackColor = dark ? Color.FromArgb(48, 48, 48) : Color.White;
        ForeColor = dark ? Color.White : Color.FromArgb(33, 33, 33);
        Controls.Add(new Label
        {
            Text = L.MainForm_SigningOut,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 11F, FontStyle.Regular),
        });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        ControlPaint.DrawBorder(e.Graphics, ClientRectangle, Color.FromArgb(120, 120, 120), ButtonBorderStyle.Solid);
    }
}
