using System.Diagnostics;
using System.Drawing;
using System.IO;
using MaterialSkin.Controls;
using QRCoder;
using RemoteAgent.Admin;

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

    // Fő nézet
    private readonly ListView _list = new();
    private readonly MaterialButton _refreshBtn = new() { Text = "Frissítés" };
    private readonly MaterialButton _connectBtn = new() { Text = "Csatlakozás" };
    private readonly MaterialButton _editBtn = new() { Text = "Szerkesztés" };
    private readonly MaterialButton _approveBtn = new() { Text = "Jóváhagyás" };
    private readonly MaterialButton _bootstrapBtn = new() { Text = "Bootstrap" };
    private readonly MaterialButton _channelsBtn = new() { Text = "Csatornák" };
    private readonly MaterialButton _usersBtn = new() { Text = "Felhasználók" };
    private readonly MaterialButton _vncLockBtn = new() { Text = "Helyi zár" };
    private readonly MaterialLabel _status = new();
    private readonly MaterialSwitch _themeSwitch = new() { Text = "Sötét" };

    public MainForm()
    {
        _cfg = ClientConfig.Load();
        ThemeManager.Skin.AddFormToManage(this);
        ThemeManager.Init(_cfg.DarkTheme);

        Text = "RemoteAppClient";
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
            var online = await _api.PingAsync();
            _onlineLbl.Text = online ? "● Online" : "● Offline";
            _onlineLbl.ForeColor = online ? Color.MediumSeaGreen : Color.IndianRed;
            SetLoginStatus(online ? "" : "A szerver nem válaszol.");
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
        var bar = new MaterialCard { Dock = DockStyle.Top, Height = 64 };
        int x = 14;
        void place(MaterialButton b, int w) { b.SetBounds(x, 12, w, 38); x += w + 8; }
        place(_refreshBtn, 110); _refreshBtn.Click += async (_, _) => await RefreshAsync();
        place(_connectBtn, 130); _connectBtn.Click += async (_, _) => await ConnectSelectedAsync();
        place(_editBtn, 120); _editBtn.Click += async (_, _) => await EditSelectedAsync();
        place(_approveBtn, 120); _approveBtn.Click += async (_, _) => await ApproveSelectedAsync();
        place(_usersBtn, 130); _usersBtn.Click += (_, _) => { if (_api is not null) new UsersForm(_api).ShowDialog(this); };
        place(_channelsBtn, 110); _channelsBtn.Click += (_, _) => { if (_api is not null) new ChannelsForm(_api).ShowDialog(this); };
        place(_bootstrapBtn, 110); _bootstrapBtn.Click += async (_, _) => await GenerateBootstrapAsync();
        place(_vncLockBtn, 150); _vncLockBtn.Click += (_, _) => ToggleLocalVncLock();
        foreach (var b in new[] { _editBtn, _approveBtn, _bootstrapBtn, _channelsBtn, _usersBtn, _vncLockBtn }) b.Visible = false;

        _themeSwitch.Checked = _cfg.DarkTheme;
        _themeSwitch.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _themeSwitch.Location = new Point(_mainView.Width - 110, 14);
        _themeSwitch.CheckedChanged += (_, _) => ApplyTheme(_themeSwitch.Checked);

        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false; _list.Dock = DockStyle.Fill;
        _list.BorderStyle = BorderStyle.None; _list.ShowItemToolTips = true;
        _list.Columns.Add("Gép", 180); _list.Columns.Add("Állapot", 90); _list.Columns.Add("Online", 60);
        _list.Columns.Add("Csoport", 110); _list.Columns.Add("Update", 60); _list.Columns.Add("Csat.", 55);
        _list.Columns.Add("Zár", 45); _list.Columns.Add("Verzió A/H/V", 130); _list.Columns.Add("Restart", 55);
        _list.Columns.Add("Utoljára látva", 130); _list.Columns.Add("deviceId", 170);
        _list.DoubleClick += async (_, _) => await ConnectSelectedAsync();

        var bottom = new MaterialCard { Dock = DockStyle.Bottom, Height = 40 };
        _status.Dock = DockStyle.Fill; _status.TextAlign = ContentAlignment.MiddleLeft; _status.Padding = new Padding(12, 0, 0, 0);
        bottom.Controls.Add(_status);
        bar.Controls.Add(_themeSwitch);

        _mainView.Controls.AddRange([_list, bottom, bar]);
        _mainView.Resize += (_, _) => _themeSwitch.Location = new Point(_mainView.Width - 110, 14);
        ApplyTheme(_cfg.DarkTheme);
    }

    private void ApplyTheme(bool dark)
    {
        _cfg.DarkTheme = dark; try { _cfg.Save(); } catch { }
        ThemeManager.SetDark(dark);
        // A sima ListView-t kézzel színezzük a témához.
        _list.BackColor = dark ? Color.FromArgb(45, 45, 48) : Color.White;
        _list.ForeColor = dark ? Color.Gainsboro : Color.Black;
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
            _login = await _api.LoginAsync(_user.Text.Trim(), _pass.Text, string.IsNullOrWhiteSpace(_totp.Text) ? null : _totp.Text.Trim());
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
        if (_role == "admin")
        {
            foreach (var b in new[] { _editBtn, _approveBtn, _bootstrapBtn, _channelsBtn, _usersBtn, _vncLockBtn }) b.Visible = true;
            UpdateVncLockBtn();
        }
        Show(_mainView);
        await RefreshAsync();
    }

    // ---------------- Fő funkciók (portolt logika) ----------------

    private async Task RefreshAsync()
    {
        if (_api is null) return;
        try
        {
            SetStatus("Eszközlista lekérése…");
            var devices = await RetryAsync(() => _api.GetDevicesAsync());
            _list.Items.Clear();
            foreach (var d in devices)
            {
                var item = new ListViewItem(string.IsNullOrEmpty(d.Hostname) ? "(névtelen)" : d.Hostname) { Tag = d };
                item.SubItems.Add(d.Status);
                item.SubItems.Add(d.Online ? "igen" : "nem");
                item.SubItems.Add(d.GroupName ?? "—");
                item.SubItems.Add(d.UpdateAllowed ? "igen" : "NEM");
                item.SubItems.Add(string.Equals(d.Channel, "beta", StringComparison.OrdinalIgnoreCase) ? "BETA" : "rtm");
                item.SubItems.Add(d.VncLocked ? "🔒" : "—");
                item.SubItems.Add($"{Short(d.AgentVersion)}/{Short(d.HelperVersion)}/{Short(d.VncVersion)}");
                item.SubItems.Add(d.AgentRestarts > 0 ? d.AgentRestarts.ToString() : "—");
                item.SubItems.Add(d.LastSeenAt?.LocalDateTime.ToString("g") ?? "—");
                item.SubItems.Add(d.DeviceId);
                if (!string.IsNullOrWhiteSpace(d.LastIncident)) item.ToolTipText = "Supervisor: " + d.LastIncident;
                _list.Items.Add(item);
            }
            SetStatus($"{devices.Count} eszköz.");
        }
        catch (Exception ex) { SetStatus("Lista hiba: " + ex.Message); }
    }

    private static string Short(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v;

    private void UpdateVncLockBtn() =>
        _vncLockBtn.Text = LocalVncLock.IsLocked() ? "Zár FELOLD" : "Helyi zár";

    private void ToggleLocalVncLock()
    {
        bool locked = LocalVncLock.IsLocked();
        var q = locked
            ? "Feloldod ezen a HELYI gépen a távoli elérést (VNC)?"
            : "Letiltod ezen a HELYI gépen a távoli elérést (VNC)?\n\nUtána erre a gépre senki sem tud távolról belépni, amíg HELYBEN fel nem oldod.";
        if (MessageBox.Show(q, "Helyi VNC-zár", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            if (LocalVncLock.RunElevated(!locked))
            {
                SetStatus(locked ? "Helyi gép feloldva." : "Helyi gép ZÁROLVA — távolról nem elérhető.");
                UpdateVncLockBtn();
            }
            else SetStatus("A művelet nem fejeződött be (UAC megszakítva?).");
        }
        catch (Exception ex) { SetStatus("Helyi zár hiba: " + ex.Message); }
    }

    private async Task ConnectSelectedAsync()
    {
        if (_api is null || _list.SelectedItems.Count == 0) { SetStatus("Válassz egy gépet."); return; }
        var sel = (DeviceInfo)_list.SelectedItems[0].Tag!;
        try
        {
            _connectBtn.Enabled = false;
            SetStatus("Friss adatok lekérése…");
            var devices = await _api.GetDevicesAsync();
            var d = devices.FirstOrDefault(x => x.DeviceId == sel.DeviceId) ?? sel;

            if (!d.Online) { MessageBox.Show("A gép nincs online.", "Offline", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (string.IsNullOrEmpty(d.VncSecret)) { MessageBox.Show("Nincs VNC-jelszó a géphez (még nem jelentette).", "Nincs jelszó", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            SetStatus($"Tunnel nyitása: {d.Hostname}…");
            var result = await _api.OpenTunnelAsync(d.DeviceId);
            if (result is null) { SetStatus("A tunnel-kérés sikertelen."); return; }

            SetStatus("Bástya-port elérése a helyi agenten át…");
            await Task.Delay(1500);
            var localPort = await _broker!.ForwardAsync(result.RemotePort);
            LaunchViewer(localPort, d.VncSecret!);
            SetStatus($"VNC indítva: {d.Hostname}");
        }
        catch (Exception ex) { SetStatus("Csatlakozási hiba: " + ex.Message); }
        finally { _connectBtn.Enabled = true; }
    }

    private async Task ApproveSelectedAsync()
    {
        if (_api is null || _list.SelectedItems.Count == 0) return;
        var sel = (DeviceInfo)_list.SelectedItems[0].Tag!;
        if (string.Equals(sel.Status, "Approved", StringComparison.OrdinalIgnoreCase)) { SetStatus($"{sel.Hostname} már jóváhagyott."); return; }
        if (MessageBox.Show($"Jóváhagyod ezt a gépet?\n\n{sel.Hostname}\n{sel.DeviceId}", "Jóváhagyás", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { await _api.ApproveDeviceAsync(sel.DeviceId); SetStatus($"{sel.Hostname} jóváhagyva."); await RefreshAsync(); }
        catch (Exception ex) { SetStatus("Jóváhagyás hiba: " + ex.Message); }
    }

    private async Task GenerateBootstrapAsync()
    {
        if (_api is null) return;
        try
        {
            var blob = await _api.CreateBootstrapAsync(maxUses: 100000, expiresInHours: null);
            if (string.IsNullOrWhiteSpace(blob)) { SetStatus("Bootstrap: üres válasz."); return; }
            try { Clipboard.SetText(blob); } catch { }
            MessageBox.Show(
                "Bootstrap blob (vágólapra másolva):\n\n" + blob +
                "\n\nTelepítés az ügyfélnél (admin):\n  RemoteAgent.exe bootstrap <blob>\n  RemoteAgent.exe install-service\n\n" +
                "A gép Pending-be kerül — itt hagyd jóvá.",
                "Bootstrap blob", MessageBoxButtons.OK, MessageBoxIcon.Information);
            SetStatus("Bootstrap blob generálva és vágólapra másolva.");
        }
        catch (Exception ex) { SetStatus("Bootstrap hiba: " + ex.Message); }
    }

    private async Task EditSelectedAsync()
    {
        if (_api is null || _list.SelectedItems.Count == 0) return;
        var d = (DeviceInfo)_list.SelectedItems[0].Tag!;
        try
        {
            var groups = await _api.GetGroupsAsync();
            using var dlg = new EditDeviceForm(d, groups);
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result is null) return;
            await _api.UpdateDeviceAsync(d.DeviceId, dlg.Result);
            await RefreshAsync();
        }
        catch (Exception ex) { SetStatus("Szerkesztés hiba: " + ex.Message); }
    }

    private void LaunchViewer(int localPort, string password)
    {
        var psi = new ProcessStartInfo(_cfg.ViewerExe) { UseShellExecute = false };
        psi.ArgumentList.Add("-host=127.0.0.1");
        psi.ArgumentList.Add($"-port={localPort}");
        psi.ArgumentList.Add($"-password={password}");
        Process.Start(psi);
    }

    private static async Task<T> RetryAsync<T>(Func<Task<T>> action, int attempts = 4)
    {
        Exception? last = null;
        for (int i = 0; i < attempts; i++)
        {
            try { return await action(); }
            catch (Exception ex) { last = ex; await Task.Delay(800); }
        }
        throw last!;
    }

    private void SetStatus(string text) => _status.Text = text;
    private void SetLoginStatus(string text) => _loginStatus.Text = text;

    private void Cleanup()
    {
        try { _api?.LogoutAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
        _broker?.Dispose();
        _api?.Dispose();
    }
}
