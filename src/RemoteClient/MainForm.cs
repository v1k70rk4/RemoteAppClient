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
/// A konzol fő ablaka (MaterialSkin). Három állapot: (1) nincs helyi agent, (2) van agent →
/// szerver-státusz + bejelentkezés, (3) belépve → eszközök + admin-funkciók. A transportot a
/// helyi agent brókere adja (a gép SSH-kulcsával); a konzol csak beléptetett gépen működik.
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

    // Nézetek
    private readonly Panel _noAgentView = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel _authView = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel _mainView = new() { Dock = DockStyle.Fill, Visible = false };

    // Auth-nézet vezérlők
    private readonly MaterialLabel _serverNameLbl = new();
    private readonly MaterialLabel _onlineLbl = new();
    private readonly MaterialLabel _remoteLbl = new();
    private readonly MaterialLabel _supportLbl = new();        // tulajdonos + support a login fejlécen
    private readonly MaterialLabel _noAgentSupportLbl = new(); // support a "nincs agent" képernyőn
    private RemoteAgent.Admin.BrandingInfo? _branding;
    private readonly MaterialCard _loginCard = new();
    private readonly MaterialCard _setupCard = new();
    private readonly MaterialTextBox2 _user = new() { Hint = L.CredentialDialog_002 };
    private readonly MaterialTextBox2 _pass = new() { Hint = L.MainForm_001, UseSystemPasswordChar = true };
    private readonly MaterialTextBox2 _totp = new() { Hint = L.MainForm_059 };
    private readonly MaterialButton _loginBtn = new() { Text = L.MainForm_002 };
    private readonly MaterialButton _helloBtn = new() { Text = L.MainForm_003, Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false, Visible = false };
    private readonly MaterialLabel _loginStatus = new() { Visible = true };
    private readonly MaterialLabel _forgotLink = new() { Text = L.MainForm_004, AutoSize = true, ForeColor = Color.DodgerBlue, Cursor = Cursors.Hand };
    private bool _loggedInViaHello;
    // Setup
    private readonly MaterialTextBox2 _newPass = new() { Hint = L.ForgotPasswordForm_004, UseSystemPasswordChar = true };
    private readonly MaterialTextBox2 _newPass2 = new() { Hint = L.MainForm_005, UseSystemPasswordChar = true };
    private readonly PictureBox _qr = new() { SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(160, 160) };
    private readonly MaterialTextBox2 _enrollCode = new() { Hint = L.MainForm_006 };
    private readonly MaterialButton _finishBtn = new() { Text = L.MainForm_007 };
    private readonly MaterialLabel _setupStatus = new();

    // Fő nézet — egyablakos: bal oldali menü + jobb oldali tartalom-host
    private readonly MaterialLabel _envLbl = new();   // élő környezet-jelző (a helyi agent status-pipe-jából)
    private readonly System.Windows.Forms.Timer _envTimer = new() { Interval = 3000 };
    private readonly MaterialLabel _verLbl = new();   // ver: x.y.z a bal alsó sarokban
    private readonly Panel _warnPanel = new() { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false };
    private readonly MaterialLabel _secretWarnLbl = new(); // secret-lejárat figyelmeztető (piros, az online felett)
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _nav = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8, 8, 8, 8) };
    private readonly List<(MaterialButton Btn, IContentView View)> _navItems = new();
    private IContentView? _currentView;

    // Nézetpéldányok (belépés után jönnek létre, amikor már van _api/_broker)
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
        try { if (Environment.ProcessPath is { } exe) Icon = Icon.ExtractAssociatedIcon(exe); } catch { /* ikon nélkül is megy */ }
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

    // Kilépéskor előbb (háttérben) lejelentkezünk a szerverről és lezárjuk a tunnelt,
    // hogy az UI ne fagyjon le. Közben egy kis „Kilépés folyamatban…" ablak látszik.
    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_cleaned) return;            // a háttér-takarítás kész → engedjük lezárni
        e.Cancel = true;                 // előbb takarítunk, csak utána zárunk

        try { _envTimer.Stop(); } catch { /* best effort */ }

        using var overlay = new ShutdownOverlay(ThemeManager.ResolveDark(_cfg.ThemeMode));
        overlay.Show(this);
        overlay.Refresh();
        Enabled = false;

        await Task.Run(Cleanup);

        _cleaned = true;
        Close();
    }

    // ---------------- Állapotváltás ----------------

    private void Show(Panel view)
    {
        foreach (var v in new[] { _noAgentView, _authView, _mainView }) v.Visible = v == view;
        view.BringToFront();
    }

    /// <summary>
    /// Friss admin-API forwardot nyit a HELYI brókeren. Ha a named pipe meghalt (pl. a gép alvása
    /// után), eldobja és újracsatlakozik, majd újrapróbál. Ezt hívja az AdminApi ConnectCallback-je,
    /// így a halott tunnel hibaüzenet helyett magától újraépül.
    /// </summary>
    private async Task<int> RefreshAdminForwardAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                _broker ??= await BrokerClient.TryConnectAsync()
                    ?? throw new InvalidOperationException(L.MainForm_008);
                return await _broker.ForwardAsync(_cfg.AdminApiPort, ct);
            }
            catch when (attempt == 0)
            {
                // A named pipe meghalhatott alvás közben — dobjuk el és csatlakozzunk újra.
                try { _broker?.Dispose(); } catch { /* best effort */ }
                _broker = null;
            }
        }
        throw new InvalidOperationException(L.MainForm_009);
    }

    private bool _envBusy;

    /// <summary>A helyi agent status-pipe-ját lekérdezi, és frissíti az élő környezet-jelzőt.</summary>
    private async Task RefreshEnvAsync()
    {
        if (_envBusy) return;
        _envBusy = true;
        try
        {
            var s = await StatusClient.QueryAgentAsync();
            if (_api is not null && !string.IsNullOrWhiteSpace(s?.DeviceId)) _api.DeviceId = s!.DeviceId;
            string text; Color color;
            if (s is null) { text = L.MainForm_010; color = Color.Gray; }
            else if (!s.C2Connected) { text = L.MainForm_060; color = Color.IndianRed; }
            else { text = s.TunnelActive ? L.MainForm_011 : "● Online"; color = Color.MediumSeaGreen; }
            if (!_envLbl.IsDisposed) { _envLbl.Text = text; _envLbl.ForeColor = color; }
        }
        catch { /* a jelző nem kritikus */ }
        finally { _envBusy = false; }
    }

    private void ApplyBranding()
    {
        // A branding a kék címsorban (Form.Text), pl. „Coimbra ITS RemoteAppClient".
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
        return parts.Count == 0 ? "" : L.MainForm_012 + string.Join("    ", parts);
    }

    /// <summary>Admin: a Graph secret 30 napon belül lejár-e → piros jelzés az online felett.</summary>
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
                        ? L.MainForm_013
                        : L.Format(L.MainForm_014, (int)days);
                    _warnPanel.Visible = true;
                    return;
                }
            }
            _warnPanel.Visible = false;
        }
        catch { /* nem kritikus */ }
    }

    /// <summary>Friss branding a szerverről (a tunnelen át), cache-be is. Csendben hibatűrő.</summary>
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
        // Cache-elt branding azonnal (bejelentkezés / agent előtt is látszik).
        _branding = BrandingCache.Load();
        ApplyBranding();

        SetLoginStatus(L.MainForm_015);
        _broker = await BrokerClient.TryConnectAsync();
        if (_broker is null) { Show(_noAgentView); return; }

        // Státusz: szerver neve + online + helyi VNC-zár.
        _serverNameLbl.Text = L.MainForm_061 + AgentInfo.ServerName();
        _remoteLbl.Text = L.MainForm_016 + (LocalVncLock.IsLocked() ? L.MainForm_066 : L.MainForm_017);
        Show(_authView);

        // Élő környezet-jelző: a helyi agent status-pipe-ját pollozzuk (C2 / tunnel valós időben).
        _envTimer.Tick += async (_, _) => await RefreshEnvAsync();
        _envTimer.Start();
        _ = RefreshEnvAsync();

        try
        {
            _api = new AdminApi(RefreshAdminForwardAsync);

            // A bróker ssh -L tunnele pár másodperccel a port lefoglalása UTÁN épül fel
            // (hideg SSH-handshake). Ne ijesszünk azonnal „nem válaszol"-lal: ~15 mp-ig pingelünk.
            _onlineLbl.Text = L.MainForm_018; _onlineLbl.ForeColor = Color.Goldenrod;
            SetLoginStatus(L.MainForm_019);
            bool online = false;
            for (int i = 0; i < 15 && !online; i++)
            {
                online = await _api.PingAsync();
                if (!online) await Task.Delay(1000);
            }
            _onlineLbl.Text = online ? "● Online" : "● Offline";
            _onlineLbl.ForeColor = online ? Color.MediumSeaGreen : Color.IndianRed;
            SetLoginStatus(online ? "" : L.MainForm_020);

            if (online) await RefreshBrandingAsync(); // friss branding a tunnelen át (login előtt is)
        }
        catch (Exception ex)
        {
            _onlineLbl.Text = "● Offline";
            _onlineLbl.ForeColor = Color.IndianRed;
            SetLoginStatus(L.MainForm_062 + ex.Message);
        }

        // Windows Hello gomb: csak ha ezen a gépen be van állítva (van credentialId) ÉS elérhető a Hello.
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
        if (_api is null) { SetLoginStatus(L.MainForm_021); return; }
        using var f = new ForgotPasswordForm(_api);
        f.ShowDialog(this);
    }

    private static string HelloKeyName(string username) => "RemoteAppClient-" + username;

    // ---------------- Nézetek építése ----------------

    private void BuildNoAgentView()
    {
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var card = new MaterialCard { Width = 470, Height = 250, Anchor = AnchorStyles.None };
        var icon = new MaterialLabel { Text = "⚠", Font = new Font("Segoe UI", 36F), AutoSize = true, Location = new Point(24, 18) };
        var title = new MaterialLabel { Text = L.MainForm_063, Font = new Font("Segoe UI", 15F, FontStyle.Bold), AutoSize = true, Location = new Point(90, 28) };
        var body = new MaterialLabel
        {
            Text = L.MainForm_022,
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
        // Státusz-fejléc
        var header = new MaterialCard { Dock = DockStyle.Top, Height = 116, Padding = new Padding(20) };
        _serverNameLbl.Font = new Font("Segoe UI", 14F, FontStyle.Bold); _serverNameLbl.AutoSize = true; _serverNameLbl.Location = new Point(20, 14);
        _onlineLbl.Text = "● …"; _onlineLbl.AutoSize = true; _onlineLbl.Location = new Point(22, 50);
        _remoteLbl.AutoSize = true; _remoteLbl.Location = new Point(160, 50);
        _supportLbl.AutoSize = true; _supportLbl.Location = new Point(22, 78);
        _supportLbl.FontType = MaterialSkin.MaterialSkinManager.fontType.Caption;
        header.Controls.AddRange([_serverNameLbl, _onlineLbl, _remoteLbl, _supportLbl]);

        // Login + setup kártya egy középre igazító TableLayoutPanelben (egy cella, 100% kitöltés).
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _loginCard.Anchor = AnchorStyles.None;
        _setupCard.Anchor = AnchorStyles.None;
        _loginCard.Size = new Size(360, 420);
        var lt = new MaterialLabel { Text = L.MainForm_023, Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Location = new Point(20, 16) };
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

        // Setup kártya (első belépés) — kezdetben rejtett
        _setupCard.Size = new Size(420, 470); _setupCard.Visible = false;
        var st = new MaterialLabel { Text = L.MainForm_024, Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Location = new Point(20, 16) };
        _newPass.SetBounds(20, 56, 380, 48);
        _newPass2.SetBounds(20, 110, 380, 48);
        var ql = new MaterialLabel { Text = L.MainForm_064, AutoSize = true, Location = new Point(20, 168) };
        _qr.Location = new Point(20, 196);
        _enrollCode.SetBounds(200, 210, 200, 48);
        _finishBtn.SetBounds(200, 270, 200, 40);
        _finishBtn.Click += async (_, _) => await DoFinishAsync();
        _setupStatus.SetBounds(20, 380, 380, 60); _setupStatus.ForeColor = Color.IndianRed;
        _setupCard.Controls.AddRange([st, _newPass, _newPass2, ql, _qr, _enrollCode, _finishBtn, _setupStatus]);

        center.Controls.Add(_loginCard, 0, 0);
        center.Controls.Add(_setupCard, 0, 0); // ugyanabba a cellába, átfedve (egyszerre egy látszik)
        _authView.Controls.AddRange([center, header]); // a center kitölt, a header felülre dokkol
    }

    private void BuildMainView()
    {
        // --- Bal oldali menü-sáv: a menü + lent az online/verzió. A branding a kék címsorban van. ---
        var sidebar = new MaterialCard { Dock = DockStyle.Left, Width = 220, Margin = new Padding(0), Padding = new Padding(0) };

        // Bal alsó sarok: Online-jelző + kliens verzió (a téma-kapcsoló átkerült a Beállításokba).
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 56 };
        _envLbl.AutoSize = true; _envLbl.MaximumSize = new Size(200, 0);
        _envLbl.Location = new Point(12, 8); _envLbl.Text = L.MainForm_025; _envLbl.ForeColor = Color.Gray;
        _verLbl.AutoSize = true; _verLbl.Location = new Point(12, 32);
        _verLbl.Text = "ver: " + ClientUpdater.RunningVersionString();
        _verLbl.FontType = MaterialSkin.MaterialSkinManager.fontType.Caption;
        footer.Controls.AddRange([_envLbl, _verLbl]);

        // Az online FELETT: secret-lejárat figyelmeztető (piros, csak ha aktuális).
        _secretWarnLbl.AutoSize = true; _secretWarnLbl.MaximumSize = new Size(200, 0);
        _secretWarnLbl.ForeColor = Color.IndianRed; _secretWarnLbl.Margin = new Padding(12, 6, 8, 6);
        _secretWarnLbl.FontType = MaterialSkin.MaterialSkinManager.fontType.Caption;
        _warnPanel.Padding = new Padding(0, 2, 0, 2);
        _warnPanel.Controls.Add(_secretWarnLbl);

        sidebar.Controls.Add(_nav);        // Fill
        sidebar.Controls.Add(_warnPanel);  // Bottom (előbb adva → a footer FÖLÉ kerül)
        sidebar.Controls.Add(footer);      // Bottom (utoljára → legalulra)

        _mainView.Controls.Add(_content); // Fill (jobb oldal)
        _mainView.Controls.Add(sidebar);  // Left
    }

    /// <summary>Egy menüpont gomb létrehozása a bal sávban + a hozzá tartozó nézet.</summary>
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

        // Aktív menüpont kiemelése.
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

    /// <summary>Téma-mód alkalmazása ("light"/"dark"/"auto") — menti, feloldja, és mindenhol érvényesíti.</summary>
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
    /// Ha a szerver kötelező frissítést írt elő (a kliens régebbi a megengedettnél): letölti+cseréli+újraindít.
    /// true = kezelve, a hívó NE lépjen tovább (vagy frissül és kilép, vagy hibát mutat és marad a loginon).
    /// </summary>
    private async Task<bool> HandleMandatoryUpdateAsync(LoginResponse login)
    {
        if (!login.MustUpdate) return false;
        SetLoginStatus(L.MainForm_026);
        if (!string.IsNullOrWhiteSpace(login.UpdateFileName)
            && await ClientUpdater.ApplyKnownAsync(_api!, login.UpdateFileName!, login.UpdateSha256))
        {
            Cleanup();
            _cleaned = true;
            Application.Exit();
            return true;
        }
        MessageBox.Show(
            L.MainForm_027,
            L.MainForm_028, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        SetLoginStatus(L.MainForm_029);
        return true;
    }

    private async Task DoLoginAsync()
    {
        SetLoginStatus("");
        if (_api is null) { SetLoginStatus(L.MainForm_065); return; }
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
                "totp_required" => L.MainForm_030,
                "totp_invalid" => L.MainForm_031,
                "invalid_credentials" => L.MainForm_032,
                "device_locked" => L.MainForm_033,
                _ => L.MainForm_034 + ex.Code,
            });
        }
        catch (Exception ex) { SetLoginStatus(L.ForgotPasswordForm_019 + ex.Message); }
        finally { _loginBtn.Enabled = true; }
    }

    private async Task DoHelloLoginAsync()
    {
        SetLoginStatus("");
        if (_api is null) { SetLoginStatus(L.MainForm_065); return; }
        if (_cfg.HelloCredentialId is not { } credId || string.IsNullOrWhiteSpace(_cfg.HelloUsername))
        { SetLoginStatus(L.MainForm_035); return; }
        var user = _cfg.HelloUsername!;
        try
        {
            _helloBtn.Enabled = false;
            SetLoginStatus("Windows Hello…");
            var challenge = await _api.HelloChallengeAsync(user);
            var sig = await WindowsHello.SignAsync(HelloKeyName(user), challenge);
            if (sig is null) { SetLoginStatus(L.MainForm_036); return; }
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
                "challenge_expired" => L.MainForm_037,
                "hello_unknown" => L.MainForm_038,
                "hello_invalid" => L.MainForm_039,
                "invalid_credentials" => L.MainForm_040,
                _ => L.MainForm_041 + ex.Code,
            });
            if (ex.Code == "hello_unknown")
            {
                _cfg.HelloCredentialId = null; _cfg.HelloUsername = null; try { _cfg.Save(); } catch { }
                _helloBtn.Visible = false;
            }
        }
        catch (Exception ex) { SetLoginStatus(L.ForgotPasswordForm_019 + ex.Message); }
        finally { _helloBtn.Enabled = true; }
    }

    /// <summary>Jelszavas belépés után felajánlja a Windows Hello beállítását ezen a gépen (egyszer).</summary>
    private async Task OfferHelloSetupAsync()
    {
        if (_api is null || _loggedInViaHello) return;
        if (_cfg.HelloCredentialId is not null) return;           // már be van állítva
        if (!await WindowsHello.IsAvailableAsync()) return;        // nincs Hello a gépen
        if (MessageBox.Show(
                L.MainForm_042 +
                L.MainForm_043,
                L.MainForm_044, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        try
        {
            var pub = await WindowsHello.CreateAsync(HelloKeyName(_username));
            if (pub is null) return; // a felhasználó megszakította a Hello-promptot
            var credId = await _api.RegisterHelloAsync(pub, Environment.MachineName);
            _cfg.HelloCredentialId = credId; _cfg.HelloUsername = _username; try { _cfg.Save(); } catch { }
            MessageBox.Show(L.MainForm_045,
                "Windows Hello", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.MainForm_046 + ex.Message, "Windows Hello", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        catch { /* a titok kézzel is beírható */ }
    }

    private async Task DoFinishAsync()
    {
        _setupStatus.Text = "";
        try
        {
            _finishBtn.Enabled = false;
            if (_login!.MustChangePassword)
            {
                if (_newPass.Text.Length < 10) { _setupStatus.Text = L.MainForm_047; return; }
                if (_newPass.Text != _newPass2.Text) { _setupStatus.Text = L.MainForm_048; return; }
                await _api!.ChangePasswordAsync(_newPass.Text);
            }
            if (_login.TotpEnrollRequired)
            {
                if (string.IsNullOrWhiteSpace(_enrollCode.Text)) { _setupStatus.Text = L.MainForm_049; return; }
                await _api!.ConfirmTotpAsync(_enrollCode.Text.Trim());
            }
            await EnterMainAsync();
        }
        catch (Exception ex) { _setupStatus.Text = L.ForgotPasswordForm_019 + ex.Message; }
        finally { _finishBtn.Enabled = true; }
    }

    /// <summary>
    /// A self-update csatornája = a SAJÁT gép eszköz-csatornája (hostname-egyezés a flottában),
    /// hogy az agent/helper/client/vnc EGYAZON beállítást kövesse. Ha a gép nincs a flottában
    /// (vagy hiba), a lokális configban tárolt csatorna a fallback. A feloldott értéket elmentjük.
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
        // Csendes önfrissítés: ha van újabb 'client' a csatornán, lecseréli magát és újraindul.
        // A csatorna a GÉP eszköz-csatornája (ugyanaz, amit az agent/helper/vnc követ), hostname
        // alapján a szerverről; fallback a lokális configra (offline / nem-flotta gép).
        if (_api is not null)
        {
            SetLoginStatus(L.MainForm_050);
            var channel = await ResolveUpdateChannelAsync();
            if (await ClientUpdater.CheckAndUpdateAsync(_api, channel)) { Cleanup(); _cleaned = true; Application.Exit(); return; }
        }

        ApplyBranding(); // kék címsor branding

        // Nézetek + menü létrehozása a jogosultság szerint (operator csak az Eszközöket látja).
        _devicesView = new DevicesView(_api!, _broker!, _cfg, _role == "admin");
        AddNav(L.MainForm_051, _devicesView);
        if (_role == "admin")
        {
            _usersView = new UsersView(_api!, _username);
            _groupsView = new GroupsView(_api!);
            _channelsView = new ChannelsView(_api!);
            _bootstrapView = new BootstrapView(_api!);
            _logView = new LogView(_api!);
            _serverSettingsView = new ServerSettingsView(_api!);
            AddNav(L.MainForm_052, _usersView);
            AddNav("Csoportok", _groupsView);
            AddNav(L.MainForm_053, _channelsView);
            AddNav("Bootstrap", _bootstrapView);
            AddNav(L.MainForm_054, _logView);
            AddNav(L.MainForm_055, _serverSettingsView);
            _ = CheckSecretExpiryAsync();
        }

        // Beállítások + Névjegy MINDENKINEK (lokális beállítások; nem admin-függő).
        _settingsView = new SettingsView(_cfg.ThemeMode, ApplyThemeMode, _role == "admin");
        _aboutView = new AboutView(_cfg);
        AddNav(L.MainForm_056, _settingsView);
        AddNav(L.MainForm_057, _aboutView);

        ApplyThemeMode(_cfg.ThemeMode);
        Show(_mainView);
        await SwitchToAsync(_devicesView);

        // Jelszavas belépés után (ha még nincs) felajánljuk a Windows Hello beállítását ezen a gépen.
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

/// <summary>Kis, keret nélküli „Kilépés folyamatban…" jelző a leállítás idejére.</summary>
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
            Text = L.MainForm_058,
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
