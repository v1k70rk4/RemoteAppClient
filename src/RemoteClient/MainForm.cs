using System.Diagnostics;
using System.Drawing;
using System.IO;
using MaterialSkin.Controls;
using QRCoder;
using RemoteAgent.Admin;
using RemoteClient.Views;

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
    private readonly MaterialCard _loginCard = new();
    private readonly MaterialCard _setupCard = new();
    private readonly MaterialTextBox2 _user = new() { Hint = "Felhasználó" };
    private readonly MaterialTextBox2 _pass = new() { Hint = "Jelszó", UseSystemPasswordChar = true };
    private readonly MaterialTextBox2 _totp = new() { Hint = "TOTP (ha van)" };
    private readonly MaterialButton _loginBtn = new() { Text = "Belépés" };
    private readonly MaterialLabel _loginStatus = new() { Visible = true };
    // Setup
    private readonly MaterialTextBox2 _newPass = new() { Hint = "Új jelszó (min. 10)", UseSystemPasswordChar = true };
    private readonly MaterialTextBox2 _newPass2 = new() { Hint = "Új jelszó újra", UseSystemPasswordChar = true };
    private readonly PictureBox _qr = new() { SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(160, 160) };
    private readonly MaterialTextBox2 _enrollCode = new() { Hint = "Hitelesítő kód" };
    private readonly MaterialButton _finishBtn = new() { Text = "Befejezés" };
    private readonly MaterialLabel _setupStatus = new();

    // Fő nézet — egyablakos: bal oldali menü + jobb oldali tartalom-host
    private readonly MaterialLabel _mainServerLbl = new();
    private readonly MaterialSwitch _themeSwitch = new() { Text = "Sötét" };
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
    private LocalLockView? _localLockView;

    public MainForm()
    {
        _cfg = ClientConfig.Load();
        ThemeManager.Skin.AddFormToManage(this);
        ThemeManager.Init(_cfg.DarkTheme);

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
        FormClosing += (_, _) => Cleanup();
    }

    // ---------------- Állapotváltás ----------------

    private void Show(Panel view)
    {
        foreach (var v in new[] { _noAgentView, _authView, _mainView }) v.Visible = v == view;
        view.BringToFront();
    }

    private async Task InitAsync()
    {
        SetLoginStatus("Helyi agent keresése…");
        _broker = await BrokerClient.TryConnectAsync();
        if (_broker is null) { Show(_noAgentView); return; }

        // Státusz: szerver neve + online + helyi VNC-zár.
        _serverNameLbl.Text = "Szerver:  " + AgentInfo.ServerName();
        _remoteLbl.Text = "Távoli elérés ezen a gépen:  " + (LocalVncLock.IsLocked() ? "LETILTVA" : "Engedélyezve");
        Show(_authView);

        try
        {
            var adminPort = await _broker.ForwardAsync(_cfg.AdminApiPort);
            _api = new AdminApi($"http://127.0.0.1:{adminPort}");

            // A bróker ssh -L tunnele pár másodperccel a port lefoglalása UTÁN épül fel
            // (hideg SSH-handshake). Ne ijesszünk azonnal „nem válaszol"-lal: ~15 mp-ig pingelünk.
            _onlineLbl.Text = "● Kapcsolódás…"; _onlineLbl.ForeColor = Color.Goldenrod;
            SetLoginStatus("Kapcsolódás a szerverhez…");
            bool online = false;
            for (int i = 0; i < 15 && !online; i++)
            {
                online = await _api.PingAsync();
                if (!online) await Task.Delay(1000);
            }
            _onlineLbl.Text = online ? "● Online" : "● Offline";
            _onlineLbl.ForeColor = online ? Color.MediumSeaGreen : Color.IndianRed;
            SetLoginStatus(online ? "" : "A szerver nem válaszol — próbálj belépni, a tunnel lehet, hogy most épül.");
        }
        catch (Exception ex)
        {
            _onlineLbl.Text = "● Offline";
            _onlineLbl.ForeColor = Color.IndianRed;
            SetLoginStatus("Csatorna hiba: " + ex.Message);
        }
    }

    // ---------------- Nézetek építése ----------------

    private void BuildNoAgentView()
    {
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var card = new MaterialCard { Width = 470, Height = 210, Anchor = AnchorStyles.None };
        var icon = new MaterialLabel { Text = "⚠", Font = new Font("Segoe UI", 36F), AutoSize = true, Location = new Point(24, 18) };
        var title = new MaterialLabel { Text = "Nincs helyi agent", Font = new Font("Segoe UI", 15F, FontStyle.Bold), AutoSize = true, Location = new Point(90, 28) };
        var body = new MaterialLabel
        {
            Text = "Ezen a gépen nem fut a RemoteAgent szolgáltatás,\nezért a konzol nem használható.\n\nTelepítsd (újra) az agentet, majd indítsd újra a klienst.",
            AutoSize = false, Location = new Point(28, 84), Size = new Size(414, 110),
        };
        card.Controls.AddRange([icon, title, body]);
        center.Controls.Add(card);
        _noAgentView.Controls.Add(center);
    }

    private void BuildAuthView()
    {
        // Státusz-fejléc
        var header = new MaterialCard { Dock = DockStyle.Top, Height = 92, Padding = new Padding(20) };
        _serverNameLbl.Font = new Font("Segoe UI", 14F, FontStyle.Bold); _serverNameLbl.AutoSize = true; _serverNameLbl.Location = new Point(20, 16);
        _onlineLbl.Text = "● …"; _onlineLbl.AutoSize = true; _onlineLbl.Location = new Point(22, 52);
        _remoteLbl.AutoSize = true; _remoteLbl.Location = new Point(160, 52);
        header.Controls.AddRange([_serverNameLbl, _onlineLbl, _remoteLbl]);

        // Login + setup kártya egy középre igazító TableLayoutPanelben (egy cella, 100% kitöltés).
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _loginCard.Anchor = AnchorStyles.None;
        _setupCard.Anchor = AnchorStyles.None;
        _loginCard.Size = new Size(360, 320);
        var lt = new MaterialLabel { Text = "Bejelentkezés", Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Location = new Point(20, 16) };
        _user.SetBounds(20, 56, 320, 48);
        _pass.SetBounds(20, 110, 320, 48);
        _totp.SetBounds(20, 164, 320, 48);
        _loginBtn.SetBounds(20, 222, 320, 40);
        _loginBtn.Click += async (_, _) => await DoLoginAsync();
        _loginStatus.SetBounds(20, 270, 320, 40); _loginStatus.ForeColor = Color.IndianRed;
        _loginCard.Controls.AddRange([lt, _user, _pass, _totp, _loginBtn, _loginStatus]);
        AcceptButton = _loginBtn;

        // Setup kártya (első belépés) — kezdetben rejtett
        _setupCard.Size = new Size(420, 470); _setupCard.Visible = false;
        var st = new MaterialLabel { Text = "Első belépés — beállítás", Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Location = new Point(20, 16) };
        _newPass.SetBounds(20, 56, 380, 48);
        _newPass2.SetBounds(20, 110, 380, 48);
        var ql = new MaterialLabel { Text = "Olvasd be authenticator appal:", AutoSize = true, Location = new Point(20, 168) };
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
        // --- Bal oldali menü-sáv: fent a szerver neve, középen a menü, lent a téma-kapcsoló ---
        var sidebar = new MaterialCard { Dock = DockStyle.Left, Width = 220, Margin = new Padding(0), Padding = new Padding(0) };

        var brand = new Panel { Dock = DockStyle.Top, Height = 56 };
        _mainServerLbl.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        _mainServerLbl.AutoSize = false; _mainServerLbl.Dock = DockStyle.Fill;
        _mainServerLbl.TextAlign = ContentAlignment.MiddleLeft; _mainServerLbl.Padding = new Padding(14, 0, 8, 0);
        brand.Controls.Add(_mainServerLbl);

        var themeRow = new Panel { Dock = DockStyle.Bottom, Height = 52 };
        _themeSwitch.Checked = _cfg.DarkTheme;
        _themeSwitch.AutoSize = true; _themeSwitch.Location = new Point(12, 12);
        _themeSwitch.CheckedChanged += (_, _) => ApplyTheme(_themeSwitch.Checked);
        themeRow.Controls.Add(_themeSwitch);

        sidebar.Controls.Add(_nav);       // Fill
        sidebar.Controls.Add(themeRow);   // Bottom
        sidebar.Controls.Add(brand);      // Top

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

    private void ApplyTheme(bool dark)
    {
        _cfg.DarkTheme = dark; try { _cfg.Save(); } catch { }
        ThemeManager.SetDark(dark);
        _content.BackColor = ThemeManager.Background;
        foreach (var (_, v) in _navItems) v.ApplyTheme();
        Invalidate(true);
    }

    // ---------------- Login + setup ----------------

    private async Task DoLoginAsync()
    {
        SetLoginStatus("");
        if (_api is null) { SetLoginStatus("Nincs kapcsolat a szerverrel."); return; }
        try
        {
            _loginBtn.Enabled = false;
            _username = _user.Text.Trim();
            _login = await _api.LoginAsync(_username, _pass.Text, string.IsNullOrWhiteSpace(_totp.Text) ? null : _totp.Text.Trim());
            _api.SetToken(_login.Token);
            _role = _login.Role;

            if (_login.MustChangePassword || _login.TotpEnrollRequired) { EnterSetup(); return; }
            await EnterMainAsync();
        }
        catch (AuthException ex)
        {
            SetLoginStatus(ex.Code switch
            {
                "totp_required" => "Add meg a TOTP kódot.",
                "totp_invalid" => "Hibás TOTP kód.",
                "invalid_credentials" => "Hibás felhasználónév vagy jelszó.",
                _ => "Bejelentkezés sikertelen: " + ex.Code,
            });
        }
        catch (Exception ex) { SetLoginStatus("Hiba: " + ex.Message); }
        finally { _loginBtn.Enabled = true; }
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
                if (_newPass.Text.Length < 10) { _setupStatus.Text = "A jelszó legyen min. 10 karakter."; return; }
                if (_newPass.Text != _newPass2.Text) { _setupStatus.Text = "A két jelszó nem egyezik."; return; }
                await _api!.ChangePasswordAsync(_newPass.Text);
            }
            if (_login.TotpEnrollRequired)
            {
                if (string.IsNullOrWhiteSpace(_enrollCode.Text)) { _setupStatus.Text = "Add meg a hitelesítő kódot."; return; }
                await _api!.ConfirmTotpAsync(_enrollCode.Text.Trim());
            }
            await EnterMainAsync();
        }
        catch (Exception ex) { _setupStatus.Text = "Hiba: " + ex.Message; }
        finally { _finishBtn.Enabled = true; }
    }

    private async Task EnterMainAsync()
    {
        // Csendes önfrissítés: ha van újabb 'client' a csatornán, lecseréli magát és újraindul.
        if (_api is not null)
        {
            SetLoginStatus("Frissítés keresése…");
            if (await ClientUpdater.CheckAndUpdateAsync(_api, _cfg.Channel)) { Cleanup(); Application.Exit(); return; }
        }

        _mainServerLbl.Text = AgentInfo.ServerName();

        // Nézetek + menü létrehozása a jogosultság szerint (operator csak az Eszközöket látja).
        _devicesView = new DevicesView(_api!, _broker!, _cfg, _role == "admin");
        AddNav("Eszközök", _devicesView);
        if (_role == "admin")
        {
            _usersView = new UsersView(_api!, _username);
            _groupsView = new GroupsView(_api!);
            _channelsView = new ChannelsView(_api!);
            _bootstrapView = new BootstrapView(_api!);
            _localLockView = new LocalLockView();
            AddNav("Felhasználók", _usersView);
            AddNav("Csoportok", _groupsView);
            AddNav("Csatornák / MSI", _channelsView);
            AddNav("Bootstrap", _bootstrapView);
            AddNav("Helyi zár", _localLockView);
        }

        ApplyTheme(_cfg.DarkTheme);
        Show(_mainView);
        await SwitchToAsync(_devicesView);
    }

    private void SetLoginStatus(string text) => _loginStatus.Text = text;

    private void Cleanup()
    {
        try { _api?.LogoutAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
        _broker?.Dispose();
        _api?.Dispose();
    }
}
