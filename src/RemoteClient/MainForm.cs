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
    private string _viewerScale = "auto";   // operator's TightVNC viewer scale; loaded at sign-in, roams with the account
    private string _viewerColor = "full";   // operator's TightVNC color depth ("full"/"256"); loaded at sign-in, roams
    private string _username = "";
    private bool _started;
    private LoginResponse? _login;
    private bool _reauthPrompting;   // show the "session expired, sign in again" prompt (and restart) only once

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
    private readonly Panel _loginCard = new();
    private readonly Panel _setupCard = new();
    private Panel? _brandPanel;
    private readonly TextField _user = new(L.CredentialDialog_User, 320);
    private readonly TextField _pass = new(L.MainForm_Password, 320, password: true);
    private readonly TextField _totp = new("000 000", 320, mono: true);
    private readonly UiToggle _remember = new(L.MainForm_RememberDevice);
    private readonly UiButton _loginBtn = new(L.MainForm_SignIn);
    private readonly UiButton _helloBtn = new(L.MainForm_SignInWithWindowsHello, UiButton.Style.Outline, "person") { Visible = false };
    private readonly Label _loginStatus = new() { Visible = true, AutoSize = false };
    private readonly Label _forgotLink = new() { Text = L.MainForm_ForgotPassword, AutoSize = true, Cursor = Cursors.Hand };
    private bool _loggedInViaHello;
    // Setup
    private readonly TextField _newPass = new(L.ForgotPasswordForm_NewPasswordMin10, 380, password: true);
    private readonly TextField _newPass2 = new(L.MainForm_RepeatNewPassword, 380, password: true);
    private readonly PictureBox _qr = new() { SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(160, 160) };
    private readonly TextField _enrollCode = new(L.MainForm_AuthenticatorCode, 200, mono: true);
    private readonly UiButton _finishBtn = new(L.MainForm_Finish);
    private readonly MaterialLabel _setupStatus = new();

    // Main view: left menu plus right content host in one window.
    private readonly MaterialLabel _envLbl = new();   // live environment indicator from local agent status pipe
    private readonly System.Windows.Forms.Timer _envTimer = new() { Interval = 3000 };
    private readonly MaterialLabel _verLbl = new();   // ver: x.y.z in the lower-left corner
    private readonly Panel _warnPanel = new() { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false };
    private readonly MaterialLabel _secretWarnLbl = new(); // secret expiry warning above online status
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _nav = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8, 8, 8, 8) };
    private readonly List<(NavItem Btn, IContentView View)> _navItems = new();
    private Panel? _sidebar;
    private Panel? _sidebarFooter;
    private Panel? _contentHost;
    private TopBar? _topBar;
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
        Width = 1280; Height = 760;
        StartPosition = FormStartPosition.CenterScreen;
        // Minimum sized so the full (compact) sidebar fits comfortably; if a smaller/scaled screen forces
        // it lower, OnLoad clamps to the working area and the nav scrolls vertically.
        MinimumSize = new Size(1180, 660);

        BuildNoAgentView();
        BuildAuthView();
        BuildMainView();
        Controls.AddRange([_mainView, _authView, _noAgentView]);

        Shown += async (_, _) => { if (!_started) { _started = true; await InitAsync(); } };
        FormClosing += OnFormClosing;
    }

    // The client ships DpiUnaware (csproj) so the owner-drawn redesign stays proportional under Windows'
    // bitmap stretch. At high display scaling the *logical* desktop we're given shrinks (e.g. ~960x540 at
    // 200%), so the fixed 1280x760 window would spill off-screen. Clamp the minimum + current size to the
    // working area and re-center; the grouped nav scrolls vertically when the sidebar can't show every item.
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var wa = Screen.FromControl(this).WorkingArea;
        if (MinimumSize.Width > wa.Width || MinimumSize.Height > wa.Height)
            MinimumSize = new Size(Math.Min(MinimumSize.Width, wa.Width), Math.Min(MinimumSize.Height, wa.Height));
        int w = Math.Min(Width, wa.Width), h = Math.Min(Height, wa.Height);
        if (w != Width || h != Height)
        {
            Size = new Size(w, h);
            Location = new Point(wa.X + Math.Max(0, (wa.Width - w) / 2), wa.Y + Math.Max(0, (wa.Height - h) / 2));
        }
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
            : $"RemoteAppClient — {_branding!.OwnerName}";

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
            _api.Unauthorized += OnSessionExpired;   // a 401 on a signed-in call means the session expired -> re-auth

            // Broker ssh -L may become usable a few seconds after reserving the port due to
            // cold SSH handshake. Avoid false "not responding" warnings by pinging for about 15s.
            _onlineLbl.Text = L.MainForm_Connecting; _onlineLbl.ForeColor = Color.Goldenrod; _brandPanel?.Invalidate();
            SetLoginStatus(L.MainForm_ConnectingToTheServer);
            bool online = false;
            for (int i = 0; i < 15 && !online; i++)
            {
                online = await _api.PingAsync();
                if (!online) await Task.Delay(1000);
            }
            _onlineLbl.Text = online ? "● Online" : "● Offline";
            _onlineLbl.ForeColor = online ? Color.MediumSeaGreen : Color.IndianRed;
            _brandPanel?.Invalidate();
            SetLoginStatus(online ? "" : L.MainForm_TheServerIsNotResponding);

            if (online) await RefreshBrandingAsync(); // fresh branding through tunnel, even before login
        }
        catch (Exception ex)
        {
            _onlineLbl.Text = "● Offline";
            _onlineLbl.ForeColor = Color.IndianRed;
            _brandPanel?.Invalidate();
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
        _noAgentView.BackColor = ThemeManager.Bg;
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, BackColor = ThemeManager.Bg };
        center.Paint += (_, pe) => pe.Graphics.Clear(ThemeManager.Bg);   // MaterialSkin greys plain panels on theming
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var card = new Panel { Width = 480, Height = 250, Anchor = AnchorStyles.None, BackColor = ThemeManager.Bg };
        card.Paint += (_, e) =>
        {
            UiPaint.DrawCard(e.Graphics, new Rectangle(0, 0, card.Width - 1, card.Height - 1), 12, ThemeManager.Panel, ThemeManager.BorderSoft);
            var tile = new Rectangle(24, 24, 48, 48);
            UiPaint.FillRoundedRect(e.Graphics, tile, 10, ThemeManager.WarnBg);
            using var wf = new Font("Segoe UI", 22F, FontStyle.Bold);
            TextRenderer.DrawText(e.Graphics, "!", wf, tile, ThemeManager.WarnFg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, L.MainForm_NoLocalAgent, UiFont.PageTitle, new Rectangle(88, 32, card.Width - 110, 26), ThemeManager.Text, TextFormatFlags.Left | TextFormatFlags.NoPadding);
        };
        var body = new MaterialLabel { Text = L.MainForm_RemoteAgentServiceIsNotRunning, AutoSize = false, Location = new Point(26, 90), Size = new Size(430, 96), ForeColor = ThemeManager.Text2 };
        _noAgentSupportLbl.AutoSize = false; _noAgentSupportLbl.Location = new Point(26, 196); _noAgentSupportLbl.Size = new Size(430, 44);
        _noAgentSupportLbl.ForeColor = ThemeManager.Text3;
        card.Controls.Add(body);
        card.Controls.Add(_noAgentSupportLbl);
        center.Controls.Add(card);
        _noAgentView.Controls.Add(center);
    }

    private void BuildAuthView()
    {
        _authView.BackColor = ThemeManager.Bg;

        // Left brand panel: blue gradient + logo + title + tagline + live online status (drawn from _onlineLbl).
        var brand = new Panel { Dock = DockStyle.Left, Width = 500 };
        _brandPanel = brand;
        brand.Paint += PaintBrand;
        brand.Resize += (_, _) => brand.Invalidate();

        // Right: login + setup forms (borderless, on the page bg) in one centered cell.
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, BackColor = ThemeManager.Bg };
        // MaterialSkin recolors plain panels to its grey when it (re)themes the form; force the page bg in Paint.
        center.Paint += (_, pe) => pe.Graphics.Clear(ThemeManager.Bg);
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _loginCard.BackColor = ThemeManager.Bg; _loginCard.Anchor = AnchorStyles.None;
        _setupCard.BackColor = ThemeManager.Bg; _setupCard.Anchor = AnchorStyles.None;
        _setupCard.Paint += (_, pe) => pe.Graphics.Clear(ThemeManager.Bg);
        // Owner-drawn children (UiToggle/UiButton) clear to Parent.BackColor; keep the cards' bg pinned to
        // the page colour so MaterialSkin's grey (re)theme can't bleed through behind them.
        _loginCard.BackColorChanged += (_, _) => { if (_loginCard.BackColor != ThemeManager.Bg) _loginCard.BackColor = ThemeManager.Bg; };
        _setupCard.BackColorChanged += (_, _) => { if (_setupCard.BackColor != ThemeManager.Bg) _setupCard.BackColor = ThemeManager.Bg; };
        _user.SetBounds(20, 94, 320, 42);
        _pass.SetBounds(20, 168, 320, 42);
        _totp.SetBounds(20, 242, 320, 42);
        _remember.Checked = !string.IsNullOrEmpty(_cfg.TrustToken);
        _user.Text = _cfg.TrustUsername ?? "";
        _loginBtn.Click += async (_, _) => await DoLoginAsync();
        _helloBtn.Click += async (_, _) => await DoHelloLoginAsync();
        _forgotLink.Click += (_, _) => OpenForgotPassword();
        _forgotLink.ForeColor = ThemeManager.Accent;
        _forgotLink.Font = UiFont.Body;
        _loginStatus.ForeColor = ThemeManager.DangerFg;
        _loginStatus.Font = UiFont.Small;
        // Plain Labels on the dark page bg; pin their bg to the page colour (and re-assert if MaterialSkin
        // resets it on (re)theme) so no grey box shows behind them.
        _forgotLink.BackColor = _loginStatus.BackColor = ThemeManager.Bg;
        _forgotLink.BackColorChanged += (_, _) => { if (_forgotLink.BackColor != ThemeManager.Bg) _forgotLink.BackColor = ThemeManager.Bg; };
        _loginStatus.BackColorChanged += (_, _) => { if (_loginStatus.BackColor != ThemeManager.Bg) _loginStatus.BackColor = ThemeManager.Bg; };
        // MaterialSkin also resets the text colour on (re)theme — keep the link blue and the status red.
        _forgotLink.ForeColorChanged += (_, _) => { if (_forgotLink.ForeColor != ThemeManager.Accent) _forgotLink.ForeColor = ThemeManager.Accent; };
        _loginStatus.ForeColorChanged += (_, _) => { if (_loginStatus.ForeColor != ThemeManager.DangerFg) _loginStatus.ForeColor = ThemeManager.DangerFg; };
        // Trusted device: hide the TOTP field (90-day skip). It reappears if the typed username
        // is not the trusted one, or if the server still demands a code (expired/revoked trust).
        _user.TextChanged += (_, _) => LayoutAuthCard(!IsTrustedUser(_user.Text.Trim()));
        LayoutAuthCard(!IsTrustedUser(_user.Text.Trim()));
        // Field labels + subtitle drawn on the (borderless) login panel above each input.
        _loginCard.Paint += (_, pe) =>
        {
            pe.Graphics.Clear(ThemeManager.Bg);   // force the page bg under the borderless fields (MaterialSkin greys it otherwise)
            TextRenderer.DrawText(pe.Graphics, L.MainForm_SignIn_2, UiFont.Title, new Rectangle(20, 4, 320, 30), ThemeManager.Text, TextFormatFlags.Left | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(pe.Graphics, L.MainForm_AuthSubtitle, UiFont.Small, new Rectangle(20, 40, 320, 18), ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding);
            void FieldLbl(string t, int top) => TextRenderer.DrawText(pe.Graphics, t, UiFont.Label, new Rectangle(20, top - 20, 320, 16), ThemeManager.Text2, TextFormatFlags.Left | TextFormatFlags.NoPadding);
            FieldLbl(L.CredentialDialog_User, _user.Top);
            FieldLbl(L.MainForm_Password, _pass.Top);
            if (_totp.Visible) FieldLbl(L.MainForm_AuthenticatorCode, _totp.Top);
        };
        _loginCard.Controls.AddRange([_user, _pass, _totp, _remember, _loginBtn, _helloBtn, _forgotLink, _loginStatus]);
        AcceptButton = _loginBtn;

        // Setup card for first sign-in, hidden initially.
        _setupCard.Size = new Size(420, 470); _setupCard.Visible = false;
        var st = new MaterialLabel { Text = L.MainForm_FirstSignInSetup, Font = new Font("Segoe UI", 16F, FontStyle.Bold), AutoSize = true, Location = new Point(20, 12) };
        _newPass.SetBounds(20, 56, 380, 48);
        _newPass2.SetBounds(20, 110, 380, 48);
        var ql = new MaterialLabel { Text = L.MainForm_ScanWithAnAuthenticatorApp, AutoSize = true, Location = new Point(20, 168) };
        _qr.Location = new Point(20, 196);
        _enrollCode.SetBounds(200, 210, 200, 48);
        _finishBtn.SetBounds(200, 270, 200, 40);
        _finishBtn.Click += async (_, _) => await DoFinishAsync();
        _setupStatus.SetBounds(20, 380, 380, 60); _setupStatus.ForeColor = ThemeManager.DangerFg;
        _setupCard.Controls.AddRange([st, _newPass, _newPass2, ql, _qr, _enrollCode, _finishBtn, _setupStatus]);

        center.Controls.Add(_loginCard, 0, 0);
        center.Controls.Add(_setupCard, 0, 0); // same cell, overlapped; only one visible at a time

        var formArea = new Panel { Dock = DockStyle.Fill, BackColor = ThemeManager.Bg };
        formArea.Paint += (_, pe) => pe.Graphics.Clear(ThemeManager.Bg);
        formArea.Controls.Add(center);
        _authView.Controls.Add(formArea); // fills the area right of the brand panel
        _authView.Controls.Add(brand);
    }

    /// <summary>Paints the sign-in brand panel: blue gradient + logo tile + product name + tagline + a live
    /// server/online line (read from the status labels updated by the connect flow).</summary>
    private void PaintBrand(object? sender, PaintEventArgs e)
    {
        if (_brandPanel is not { } brand) return;
        var g = e.Graphics;
        var r = brand.ClientRectangle;
        using (var grad = new System.Drawing.Drawing2D.LinearGradientBrush(r, ColorTranslator.FromHtml("#1d4f9e"), ColorTranslator.FromHtml("#102036"), 60f))
            g.FillRectangle(grad, r);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        int x = 64, y = Math.Max(40, r.Height / 2 - 132);
        var tile = new Rectangle(x, y, 54, 54);
        UiPaint.FillRoundedRect(g, tile, 14, Color.FromArgb(46, 255, 255, 255));
        UiIcons.Draw(g, "monitor", new RectangleF(tile.X + 13, tile.Y + 13, 28, 28), Color.White, 1.8f);

        using (var titleFont = new Font("Segoe UI", 26F, FontStyle.Bold))
            TextRenderer.DrawText(g, "RemoteAppClient", titleFont, new Rectangle(x, y + 70, 380, 42), Color.White, TextFormatFlags.Left | TextFormatFlags.NoPadding);

        var owner = _branding?.OwnerName;
        var tag = string.IsNullOrWhiteSpace(owner) ? L.MainForm_AuthTagline : L.MainForm_AuthTagline + "  " + owner;
        TextRenderer.DrawText(g, tag, UiFont.Body, new Rectangle(x, y + 120, 330, 50), Color.FromArgb(190, 255, 255, 255), TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

        var dotColor = _onlineLbl.ForeColor.A == 0 ? Color.FromArgb(160, 255, 255, 255) : _onlineLbl.ForeColor;
        int sy = y + 192;
        using (var db = new SolidBrush(dotColor)) g.FillEllipse(db, x, sy + 3, 9, 9);
        var word = _onlineLbl.Text.Replace("●", "").Trim();
        if (word.Length == 0) word = "…";
        TextRenderer.DrawText(g, AgentInfo.ServerName() + "  ·  " + word, UiFont.MonoSmall, new Rectangle(x + 16, sy, 360, 16),
            Color.FromArgb(220, 255, 255, 255), TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }

    /// <summary>True when a device-trust token is stored for this username (TOTP can be skipped).</summary>
    private bool IsTrustedUser(string user) =>
        !string.IsNullOrEmpty(_cfg.TrustToken) &&
        string.Equals(_cfg.TrustUsername, user, StringComparison.OrdinalIgnoreCase);

    /// <summary>Positions the login controls, collapsing the TOTP row when it is hidden.</summary>
    private void LayoutAuthCard(bool totpVisible)
    {
        _totp.Visible = totpVisible;
        int shift = totpVisible ? 0 : 74;
        _remember.SetBounds(20, 300 - shift, 320, 30);
        _loginBtn.SetBounds(20, 338 - shift, 320, 44);
        _helloBtn.SetBounds(20, 390 - shift, 320, 44);
        _forgotLink.Location = new Point(130, 444 - shift);
        _loginStatus.SetBounds(20, 472 - shift, 320, 50);
        _loginCard.Size = new Size(360, 532 - shift);
        _loginCard.Invalidate();
    }

    private void BuildMainView()
    {
        // --- Left sidebar (redesign): logo header + grouped nav + live status footer. 236px, panel bg,
        //     1px right border. See design_handoff_console_redesign. ---
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 236, BackColor = ThemeManager.Panel, Margin = new Padding(0), Padding = new Padding(0) };
        _sidebar = sidebar;
        sidebar.Paint += (_, e) =>
        {
            using var pen = new Pen(ThemeManager.BorderSoft);
            e.Graphics.DrawLine(pen, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height);
        };

        _nav.BackColor = ThemeManager.Panel;
        _nav.Padding = new Padding(8, 8, 8, 8);
        // Vertical scrollbar only when the sidebar is too short to show every item (high DPI / small
        // window); FitNavWidth keeps items at the inner width so no horizontal bar ever appears.
        _nav.AutoScroll = true;
        _nav.ClientSizeChanged += (_, _) => FitNavWidth();

        // Footer: live status (agent status pipe) + client version.
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = ThemeManager.Panel };
        _sidebarFooter = footer;
        _envLbl.AutoSize = true; _envLbl.MaximumSize = new Size(212, 0);
        _envLbl.Location = new Point(14, 9); _envLbl.Text = L.MainForm_Environment; _envLbl.ForeColor = ThemeManager.Text2;
        _verLbl.AutoSize = true; _verLbl.Location = new Point(14, 31);
        _verLbl.Text = "ver " + ClientUpdater.RunningVersionString();
        _verLbl.FontType = MaterialSkin.MaterialSkinManager.fontType.Caption;
        footer.Controls.AddRange([_envLbl, _verLbl]);

        // Above the footer: secret-expiry warning, amber and visible only when relevant.
        _secretWarnLbl.AutoSize = true; _secretWarnLbl.MaximumSize = new Size(212, 0);
        _secretWarnLbl.ForeColor = ThemeManager.WarnFg; _secretWarnLbl.Margin = new Padding(14, 6, 8, 6);
        _secretWarnLbl.FontType = MaterialSkin.MaterialSkinManager.fontType.Caption;
        _warnPanel.Padding = new Padding(0, 2, 0, 2);
        _warnPanel.BackColor = ThemeManager.Panel;
        _warnPanel.Controls.Add(_secretWarnLbl);

        var header = new SidebarHeader(_branding?.OwnerName);

        sidebar.Controls.Add(_nav);        // Fill
        sidebar.Controls.Add(_warnPanel);  // Bottom; above footer
        sidebar.Controls.Add(footer);      // Bottom; very bottom
        sidebar.Controls.Add(header);      // Top

        _contentHost = new Panel { Dock = DockStyle.Fill };
        _contentHost.Controls.Add(_content);   // Fill; the topbar is added above it after sign-in
        _mainView.Controls.Add(_contentHost);  // Fill (right)
        _mainView.Controls.Add(sidebar);       // Left
    }

    /// <summary>Adds an uppercase nav section caption (Fleet / Manage / System).</summary>
    private void AddNavSection(string caption) => _nav.Controls.Add(new NavCaption(caption));

    /// <summary>Sizes nav items/captions to the sidebar's inner width so there is no horizontal scrollbar.</summary>
    private void FitNavWidth()
    {
        int w = _nav.ClientSize.Width;
        if (w <= 0) return;
        foreach (Control c in _nav.Controls) if (c.Width != w) c.Width = w;
    }

    /// <summary>Creates a sidebar nav item (icon + label) and wires it to its view.</summary>
    private void AddNav(string text, IContentView view, string icon)
    {
        var item = new NavItem(icon, text);
        item.Click += async (_, _) => await SwitchToAsync(view);
        _nav.Controls.Add(item);
        _navItems.Add((item, view));
    }

    /// <summary>Topbar theme toggle: flips dark/light and persists it (same path as Settings).</summary>
    private void ToggleTheme() => ApplyThemeMode(ThemeManager.IsDark ? "light" : "dark");

    /// <summary>Topbar user chip: confirm, log out the session, and return to the sign-in screen.</summary>
    private async Task SignOutAsync()
    {
        if (MessageBox.Show(this, L.MainForm_SignOutConfirm, "RemoteAppClient",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _reauthPrompting = true;   // an explicit sign-out shouldn't also fire the session-expired prompt
        try { if (_api is not null) await _api.LogoutAsync(); } catch { /* best effort */ }
        Application.Restart();
    }

    /// <summary>
    /// A signed-in API call returned 401: the operator session expired or was revoked server-side
    /// (AuthService.SessionTtl, 8 hours). Raised on a background thread by <see cref="AdminApi"/>, so marshal
    /// to the UI, tell the operator once, and restart to the sign-in screen (the same path as sign-out). A
    /// fresh sign-in mints a new session; the dead token is dropped on restart.
    /// </summary>
    private void OnSessionExpired()
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (_reauthPrompting || _devicesView is null) return;   // only after sign-in, and only once
                _reauthPrompting = true;
                MessageBox.Show(this, L.MainForm_SessionExpired, "RemoteAppClient",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                Application.Restart();
            }));
        }
        catch { /* form already gone */ }
    }

    private async Task SwitchToAsync(IContentView view)
    {
        if (ReferenceEquals(_currentView, view)) return;
        _currentView = view;

        // Highlight active nav item (accent-soft pill + accent text) and set the topbar title + subtitle.
        string title = "";
        foreach (var (btn, v) in _navItems)
        {
            bool active = ReferenceEquals(v, view);
            btn.Active = active;
            if (active) title = btn.Text;
        }
        _topBar?.SetTitle(title, view.Subtitle ?? "");

        var ctl = (Control)view;
        ctl.Dock = DockStyle.Fill;
        _content.Controls.Clear();
        _content.Controls.Add(ctl);
        view.ApplyTheme();
        await view.OnShownAsync();
        _topBar?.SetTitle(title, view.Subtitle ?? "");   // refresh subtitle once the view has loaded its data
    }

    /// <summary>Applies theme mode ("light"/"dark"/"auto"), saves it, resolves it, and applies everywhere.</summary>
    private void ApplyThemeMode(string mode)
    {
        _cfg.ThemeMode = mode; try { _cfg.Save(); } catch { }
        ThemeManager.SetDark(ThemeManager.ResolveDark(mode));
        _content.BackColor = ThemeManager.Bg;
        if (_sidebar is not null)
        {
            _sidebar.BackColor = _nav.BackColor = ThemeManager.Panel;
            if (_sidebarFooter is not null) _sidebarFooter.BackColor = ThemeManager.Panel;
            _warnPanel.BackColor = ThemeManager.Panel;
            _envLbl.ForeColor = ThemeManager.Text2;
            _secretWarnLbl.ForeColor = ThemeManager.WarnFg;
            _sidebar.Invalidate(true);
        }
        if (_topBar is not null)
        {
            _topBar.BackColor = ThemeManager.Panel;
            _topBar.Toggle.IconKey = ThemeManager.IsDark ? "moon" : "sun";
            _topBar.Invalidate(true);
        }
        foreach (var (_, v) in _navItems) { v.ApplyTheme(); RethemePlainPanels((Control)v); }
        Invalidate(true);
    }

    // MaterialSkin re-greys every plain layout Panel on the managed main form when the theme (re)applies.
    // Owner-drawn controls and Cards repaint their own bg, but plain Panels keep the grey — restore their
    // inherited page bg so the surface reads as the redesign background again after a theme switch.
    private static void RethemePlainPanels(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Card or CardPanel) continue;   // own their bg via OnPaintBackground; skip the subtree
            if (c is Panel) c.ResetBackColor();      // plain Panel / Flow / Table: drop grey, inherit the view bg
            RethemePlainPanels(c);
        }
    }

    // Applies changed viewer preferences to the live Devices view. SettingsView already persisted them server-side.
    private void ApplyViewerPrefs(string scale, string color)
    {
        _viewerScale = string.IsNullOrWhiteSpace(scale) ? "auto" : scale;
        _viewerColor = string.IsNullOrWhiteSpace(color) ? "full" : color;
        _devicesView?.SetViewerScale(_viewerScale);
        _devicesView?.SetViewerColor(_viewerColor);
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
            var trust = string.Equals(_cfg.TrustUsername, _username, StringComparison.OrdinalIgnoreCase) ? _cfg.TrustToken : null;
            _login = await _api.LoginAsync(_username, _pass.Text, string.IsNullOrWhiteSpace(_totp.Text) ? null : _totp.Text.Trim(),
                ClientUpdater.RunningVersionString(), _cfg.Channel, trust, _remember.Checked);
            if (await HandleMandatoryUpdateAsync(_login)) return;
            // Persist a newly issued device-trust token so next time TOTP can be skipped (90 days).
            if (!string.IsNullOrEmpty(_login.TrustToken))
            {
                _cfg.TrustToken = _login.TrustToken; _cfg.TrustUsername = _username;
                try { _cfg.Save(); } catch { /* best effort */ }
            }
            _api.SetToken(_login.Token);
            _role = _login.Role;

            if (_login.MustChangePassword || _login.TotpEnrollRequired) { EnterSetup(); return; }
            await EnterMainAsync();
        }
        catch (AuthException ex)
        {
            if (ex.Code is "totp_required" or "totp_invalid")
            {
                // A code is demanded but the device was treated as trusted: the stored trust is
                // stale (expired/revoked). Drop it and reveal the TOTP field so the user can sign in.
                if (!string.IsNullOrEmpty(_cfg.TrustToken))
                {
                    _cfg.TrustToken = null; _cfg.TrustUsername = null;
                    try { _cfg.Save(); } catch { /* best effort */ }
                }
                LayoutAuthCard(true);
                _totp.Focus();
            }
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

        // Operator viewer preferences (roam with the account); applied when launching the VNC viewer.
        _viewerScale = string.IsNullOrWhiteSpace(_login?.ViewerScale) ? "auto" : _login!.ViewerScale!;
        _viewerColor = string.IsNullOrWhiteSpace(_login?.ViewerColor) ? "full" : _login!.ViewerColor!;

        // Create views according to role; operators only see Devices / Settings / About.
        _devicesView = new DevicesView(_api!, _broker!.ForwardAsync, _cfg, _role == "admin", _viewerScale, _viewerColor);
        _settingsView = new SettingsView(_cfg.ThemeMode, ApplyThemeMode, _role == "admin", _api!, _viewerScale, _viewerColor, ApplyViewerPrefs,
            _cfg.VncPanelMode, mode => { _cfg.VncPanelMode = mode; try { _cfg.Save(); } catch { /* best effort */ } });
        _aboutView = new AboutView(_cfg);
        if (_role == "admin")
        {
            _usersView = new UsersView(_api!, _username);
            _groupsView = new GroupsView(_api!);
            _channelsView = new ChannelsView(_api!);
            _bootstrapView = new BootstrapView(_api!);
            _logView = new LogView(_api!);
            _serverSettingsView = new ServerSettingsView(_api!);
        }

        // Sidebar nav, grouped Manage / System (handoff). Operators get a reduced set. Devices is the home
        // item and sits directly under the header without its own caption — dropping the single-item "Fleet"
        // section also keeps the compact sidebar on one screen at high DPI (no scrollbar).
        AddNav(L.MainForm_Devices, _devicesView, "monitor");
        if (_role == "admin")
        {
            AddNavSection(L.MainForm_NavManage);
            AddNav(L.MainForm_Users, _usersView!, "users");
            AddNav(L.MainForm_Groups, _groupsView!, "layers");
            AddNav(L.MainForm_ChannelsMSI, _channelsView!, "box");
            AddNav("Bootstrap", _bootstrapView!, "terminal");
            AddNavSection(L.MainForm_NavSystem);
            AddNav(L.MainForm_Log, _logView!, "list");
            AddNav(L.MainForm_ServerSettings, _serverSettingsView!, "server");
            AddNav(L.MainForm_Settings, _settingsView, "gear");
            AddNav(L.MainForm_About, _aboutView, "info");
            _ = CheckSecretExpiryAsync();
        }
        else
        {
            AddNavSection(L.MainForm_NavSystem);
            AddNav(L.MainForm_Settings, _settingsView, "gear");
            AddNav(L.MainForm_About, _aboutView, "info");
        }

        FitNavWidth();

        // Content-area topbar: theme toggle + user chip (sign out). Created here so it has the user/role.
        var themeToggle = new IconButton(ThemeManager.IsDark ? "moon" : "sun");
        themeToggle.Click += (_, _) => ToggleTheme();
        var userChip = new UserChip(_username, _role);
        userChip.Click += async (_, _) => await SignOutAsync();
        _topBar = new TopBar(themeToggle, userChip);
        _topBar.SetStatus(L.MainForm_C2Status);
        _contentHost!.Controls.Add(_topBar);   // Top

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
