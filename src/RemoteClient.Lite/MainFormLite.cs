using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Reflection;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using RemoteClient.Views;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Lite;

/// <summary>
/// Lite operator console (viewer-only). You enter server + user + password; it signs in with an ephemeral
/// operator SSH certificate (no local SYSTEM agent — the same transport the Linux console uses), and shows
/// only Devices + Settings + About. No admin features, even for admin accounts. Keeps its own config folder.
/// </summary>
public sealed class MainFormLite : MaterialForm
{
    private const string RepoOwner = "v1k70rk4";
    private const string RepoName = "RemoteAppClient";

    private readonly ClientConfig _cfg = ClientConfig.Load();
    private LinuxOperatorTransport? _transport;
    private AdminApi? _api;
    private LoginResponse? _login;
    private string _viewerScale = "auto";
    private string _viewerColor = "full";

    // Top-level states
    private readonly Panel _authView = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel _mainView = new() { Dock = DockStyle.Fill, Visible = false };

    // Sign-in controls
    private readonly MaterialCard _loginCard = new();
    private readonly MaterialTextBox2 _server = new() { Hint = "https://server" };
    private readonly MaterialTextBox2 _user = new() { Hint = L.CredentialDialog_User };
    private readonly MaterialTextBox2 _pass = new() { Hint = L.MainForm_Password, UseSystemPasswordChar = true };
    private readonly MaterialTextBox2 _totp = new() { Hint = L.MainForm_TOTPIfAny };
    private readonly MaterialSwitch _remember = new() { Text = L.MainForm_RememberDevice, AutoSize = false, Width = 320, Height = 30 };
    private readonly MaterialButton _loginBtn = new() { Text = L.MainForm_SignIn, AutoSize = false, Width = 320, Height = 40 };
    private readonly MaterialLabel _loginStatus = new() { ForeColor = Color.IndianRed, AutoSize = true };
    private readonly MaterialLabel _updateLink = new() { AutoSize = true, ForeColor = Color.DodgerBlue, Cursor = Cursors.Hand, Visible = false };

    // Main view (sidebar nav + content host)
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _nav = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8) };
    private readonly List<(MaterialButton Btn, IContentView View)> _navItems = new();
    private IContentView? _currentView;

    private DevicesView? _devicesView;
    private SettingsView? _settingsView;
    private AboutView? _aboutView;

    public MainFormLite()
    {
        ThemeManager.Skin.AddFormToManage(this);
        ThemeManager.Init(ThemeManager.ResolveDark(_cfg.ThemeMode));

        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        Text = "Multiserver Windows RemoteAppClient Lite" + (v is null ? "" : $"  v{v.Major}.{v.Minor}.{v.Build}");
        try { if (Environment.ProcessPath is { } exe) Icon = Icon.ExtractAssociatedIcon(exe); } catch { /* icon optional */ }
        Width = 1040; Height = 640; StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(900, 560);

        BuildAuthView();
        BuildMainView();
        Controls.AddRange([_mainView, _authView]);

        _server.Text = _cfg.LastServerUrl ?? "";
        _user.Text = string.IsNullOrWhiteSpace(_cfg.LastUsername) ? (_cfg.TrustUsername ?? "") : _cfg.LastUsername;
        _remember.Checked = !string.IsNullOrEmpty(_cfg.TrustToken);
        _totp.Visible = !IsTrustedUser(_user.Text.Trim());

        Show(_authView);
        Shown += async (_, _) => await CheckForUpdateAsync();
        FormClosing += (_, _) => Cleanup();
    }

    private void Show(Panel view)
    {
        foreach (var v in new[] { _authView, _mainView }) v.Visible = v == view;
        view.BringToFront();
    }

    /// <summary>True when a device-trust token is stored for this username (TOTP can be skipped for 90 days).</summary>
    private bool IsTrustedUser(string user) =>
        !string.IsNullOrEmpty(_cfg.TrustToken) && string.Equals(_cfg.TrustUsername, user, StringComparison.OrdinalIgnoreCase);

    // ---------------- Sign-in ----------------

    private void BuildAuthView()
    {
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _loginCard.Size = new Size(380, 540);
        _loginCard.Anchor = AnchorStyles.None;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(20, 16, 20, 12) };

        var title = new MaterialLabel { Text = "RemoteAppClient", Font = new Font("Segoe UI", 15F, FontStyle.Bold), AutoSize = true, Margin = new Padding(4, 4, 4, 0) };
        var sub = new MaterialLabel { Text = "Multiserver Windows · Lite", AutoSize = true, Margin = new Padding(4, 0, 4, 8), FontType = MaterialSkin.MaterialSkinManager.fontType.Caption };
        _updateLink.Margin = new Padding(4, 0, 4, 8);
        _updateLink.Click += (_, _) => OnOpenReleases();

        foreach (var box in new MaterialTextBox2[] { _server, _user, _pass, _totp }) { box.Width = 320; box.Margin = new Padding(4, 6, 4, 6); }
        _remember.Margin = new Padding(4, 6, 4, 6);
        _loginBtn.Margin = new Padding(4, 8, 4, 6);
        _loginStatus.MaximumSize = new Size(320, 0); _loginStatus.Margin = new Padding(4, 6, 4, 6);

        _loginBtn.Click += async (_, _) => await DoLoginAsync();
        _user.TextChanged += (_, _) => _totp.Visible = !IsTrustedUser(_user.Text.Trim());

        flow.Controls.AddRange([title, sub, _updateLink, _server, _user, _pass, _totp, _remember, _loginBtn, _loginStatus]);
        _loginCard.Controls.Add(flow);
        center.Controls.Add(_loginCard, 0, 0);
        _authView.Controls.Add(center);
        AcceptButton = _loginBtn;
    }

    private async Task DoLoginAsync()
    {
        _loginStatus.ForeColor = Color.IndianRed;
        var server = _server.Text.Trim();
        var user = _user.Text.Trim();
        if (server.Length == 0 || user.Length == 0) { _loginStatus.Text = L.MainForm_NoConnectionToTheServer_2; return; }

        _loginBtn.Enabled = false;
        _loginStatus.ForeColor = Color.Goldenrod;
        _loginStatus.Text = L.MainForm_Connecting;
        try
        {
            var trust = string.Equals(_cfg.TrustUsername, user, StringComparison.OrdinalIgnoreCase) ? _cfg.TrustToken : null;
            var (transport, login) = await LinuxOperatorTransport.LoginAsync(
                server, user, _pass.Text, string.IsNullOrWhiteSpace(_totp.Text) ? null : _totp.Text.Trim(),
                trustToken: trust, rememberDevice: _remember.Checked);

            if (login.MustChangePassword || login.TotpEnrollRequired)
            {
                transport.Dispose();
                _loginStatus.ForeColor = Color.IndianRed;
                _loginStatus.Text = "Finish first-time setup (password / TOTP) in the full client first.";
                return;
            }

            _transport = transport;
            _login = login;
            _api = new AdminApi(ct => _transport!.ForwardAsync(5000, ct));
            _api.SetToken(login.Token);

            _cfg.LastServerUrl = server;
            _cfg.LastUsername = user;
            if (!string.IsNullOrWhiteSpace(login.TrustToken)) { _cfg.TrustToken = login.TrustToken; _cfg.TrustUsername = user; }
            try { _cfg.Save(); } catch { /* best effort */ }

            await EnterMainAsync();
        }
        catch (AuthException ax)
        {
            if (ax.Code is "totp_required" or "totp_invalid") { _totp.Visible = true; _totp.Focus(); }
            _loginStatus.ForeColor = Color.IndianRed;
            _loginStatus.Text = ax.Code switch
            {
                "totp_required" => L.MainForm_EnterTheTOTPCode,
                "totp_invalid" => L.MainForm_InvalidTOTPCode,
                "invalid_credentials" => L.MainForm_InvalidUsernameOrPassword,
                "device_locked" => L.MainForm_ThisDeviceIsSignIn,
                _ => L.MainForm_SignInFailed + ax.Code,
            };
        }
        catch (InvalidOperationException ie) when (ie.Message == "client_outdated")
        {
            _loginStatus.ForeColor = Color.IndianRed;
            _loginStatus.Text = L.MainForm_OutdatedClientUpdateRequired;
        }
        catch (InvalidOperationException ie) when (ie.Message == "no_operator_cert")
        {
            _loginStatus.ForeColor = Color.IndianRed;
            _loginStatus.Text = L.UsersView_KeylessOperator + ": OFF";
        }
        catch (Exception ex)
        {
            _loginStatus.ForeColor = Color.IndianRed;
            _loginStatus.Text = L.ForgotPasswordForm_Error + ex.Message;
        }
        finally { _loginBtn.Enabled = true; }
    }

    // ---------------- Main view ----------------

    private void BuildMainView()
    {
        var sidebar = new MaterialCard { Dock = DockStyle.Left, Width = 220, Margin = new Padding(0), Padding = new Padding(0) };
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 40 };
        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        var ver = new MaterialLabel
        {
            Text = "ver: " + (v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}"),
            AutoSize = true, Location = new Point(12, 10), FontType = MaterialSkin.MaterialSkinManager.fontType.Caption,
        };
        footer.Controls.Add(ver);
        sidebar.Controls.Add(_nav);
        sidebar.Controls.Add(footer);
        _mainView.Controls.Add(_content);
        _mainView.Controls.Add(sidebar);
    }

    private async Task EnterMainAsync()
    {
        // Operator viewer preferences (roam with the account); used when launching the VNC viewer.
        _viewerScale = string.IsNullOrWhiteSpace(_login?.ViewerScale) ? "auto" : _login!.ViewerScale!;
        _viewerColor = string.IsNullOrWhiteSpace(_login?.ViewerColor) ? "full" : _login!.ViewerColor!;

        // Viewer-only: isAdmin is always false here, so no admin tabs/features are ever built.
        _devicesView = new DevicesView(_api!, (port, ct) => _transport!.ForwardAsync(port, ct), _cfg, isAdmin: false, _viewerScale, _viewerColor);
        _settingsView = new SettingsView(_cfg.ThemeMode, ApplyThemeMode, isAdmin: false, _api!, _viewerScale, _viewerColor, ApplyViewerPrefs,
            _cfg.VncPanelMode, mode => { _cfg.VncPanelMode = mode; try { _cfg.Save(); } catch { /* best effort */ } });
        _aboutView = new AboutView(_cfg);

        AddNav(L.MainForm_Devices, _devicesView);
        AddNav(L.MainForm_Settings, _settingsView);
        AddNav(L.MainForm_About, _aboutView);

        ApplyThemeMode(_cfg.ThemeMode);
        Show(_mainView);
        await SwitchToAsync(_devicesView);
    }

    private void AddNav(string text, IContentView view)
    {
        var b = new MaterialButton
        {
            Text = text, AutoSize = false, Width = 200, Height = 44,
            Type = MaterialButton.MaterialButtonType.Text, HighEmphasis = false, Margin = new Padding(0, 0, 0, 4),
        };
        b.Click += async (_, _) => await SwitchToAsync(view);
        _nav.Controls.Add(b);
        _navItems.Add((b, view));
    }

    private async Task SwitchToAsync(IContentView view)
    {
        if (ReferenceEquals(_currentView, view)) return;
        _currentView = view;
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

    private void ApplyThemeMode(string mode)
    {
        _cfg.ThemeMode = mode; try { _cfg.Save(); } catch { /* best effort */ }
        ThemeManager.SetDark(ThemeManager.ResolveDark(mode));
        _content.BackColor = ThemeManager.Background;
        foreach (var (_, v) in _navItems) v.ApplyTheme();
        Invalidate(true);
    }

    private void ApplyViewerPrefs(string scale, string color)
    {
        _viewerScale = string.IsNullOrWhiteSpace(scale) ? "auto" : scale;
        _viewerColor = string.IsNullOrWhiteSpace(color) ? "full" : color;
        _devicesView?.SetViewerScale(_viewerScale);
        _devicesView?.SetViewerColor(_viewerColor);
    }

    // ---------------- GitHub update notice (no self-update on a portable Lite) ----------------

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var running = Assembly.GetEntryAssembly()?.GetName().Version;
            if (running is null) return;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RemoteAppClient-Lite");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl)) return;
            var tag = tagEl.GetString();
            var latest = ParseVersion(tag);
            if (latest is null) return;
            var run = new Version(running.Major, running.Minor, Math.Max(running.Build, 0));
            if (latest <= run) return; // up to date, or a dev build ahead of the latest release
            if (!_updateLink.IsDisposed) { _updateLink.Text = L.Format(L.LinuxConsole_UpdateAvailable, tag!) + "  →"; _updateLink.Visible = true; }
        }
        catch { /* offline / rate-limited / no releases yet - skip the notice */ }
    }

    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        return Version.TryParse(tag.TrimStart('v', 'V').Trim(), out var v)
            ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }

    private void OnOpenReleases()
    {
        try { Process.Start(new ProcessStartInfo($"https://github.com/{RepoOwner}/{RepoName}/releases/latest") { UseShellExecute = true }); }
        catch { /* no browser - ignore */ }
    }

    private void Cleanup()
    {
        try { _api?.LogoutAsync().Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        try { _api?.Dispose(); } catch { /* best effort */ }
        try { _transport?.Dispose(); } catch { /* best effort */ }
    }
}
