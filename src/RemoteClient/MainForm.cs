using System.Diagnostics;
using RemoteAgent.Admin;

namespace RemoteClient;

/// <summary>
/// Admin-konzol: eszközlista a szerverről, és egy gombbal VNC-csatlakozás —
/// open-tunnel + SSH local-forward + a viewer indítása autofill jelszóval.
/// </summary>
public sealed class MainForm : Form
{
    private readonly ClientConfig _cfg;
    private BrokerClient? _broker;
    private AdminApi? _api;

    private readonly ListView _list = new();
    private readonly Button _refreshBtn = new();
    private readonly Button _connectBtn = new();
    private readonly Button _editBtn = new();
    private readonly Button _approveBtn = new();
    private readonly Button _bootstrapBtn = new();
    private readonly Button _channelsBtn = new();
    private readonly Button _vncLockBtn = new();
    private readonly Button _usersBtn = new();
    private readonly Label _status = new();
    private string _role = "operator";

    public MainForm()
    {
        _cfg = ClientConfig.Load();

        Text = "RemoteAppClient — admin konzol";
        Width = 980;
        Height = 510;
        StartPosition = FormStartPosition.CenterScreen;

        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.Dock = DockStyle.Top;
        _list.Height = 330;
        _list.Columns.Add("Gép", 180);
        _list.Columns.Add("Állapot", 90);
        _list.Columns.Add("Online", 60);
        _list.Columns.Add("Csoport", 110);
        _list.Columns.Add("Update", 60);
        _list.Columns.Add("Csat.", 55);
        _list.Columns.Add("Zár", 45);
        _list.Columns.Add("Verzió A/H/V", 130);
        _list.Columns.Add("Restart", 55);
        _list.Columns.Add("Utoljára látva", 130);
        _list.Columns.Add("deviceId", 170);
        _list.ShowItemToolTips = true; // a supervisor utolsó incidense tooltipben
        _list.DoubleClick += async (_, _) => await ConnectSelectedAsync();

        // 1. sor — mindenkinek (operator is): frissítés + csatlakozás.
        _refreshBtn.Text = "Frissítés";
        _refreshBtn.SetBounds(12, 348, 110, 32);
        _refreshBtn.Click += async (_, _) => await RefreshAsync();

        _connectBtn.Text = "Csatlakozás (VNC)";
        _connectBtn.SetBounds(132, 348, 160, 32);
        _connectBtn.Click += async (_, _) => await ConnectSelectedAsync();

        // 1. sor — admin: szerkesztés + jóváhagyás (login után jelenik meg).
        _editBtn.Text = "Szerkesztés…";
        _editBtn.SetBounds(302, 348, 110, 32);
        _editBtn.Click += async (_, _) => await EditSelectedAsync();

        _approveBtn.Text = "Jóváhagyás";
        _approveBtn.SetBounds(422, 348, 110, 32);
        _approveBtn.Click += async (_, _) => await ApproveSelectedAsync();

        // 2. sor — admin.
        _bootstrapBtn.Text = "Bootstrap blob…";
        _bootstrapBtn.SetBounds(12, 386, 130, 32);
        _bootstrapBtn.Click += async (_, _) => await GenerateBootstrapAsync();

        _channelsBtn.Text = "Csatornák…";
        _channelsBtn.SetBounds(152, 386, 110, 32);
        _channelsBtn.Click += (_, _) => { if (_api is not null) new ChannelsForm(_api).ShowDialog(this); };

        _usersBtn.Text = "Felhasználók…";
        _usersBtn.SetBounds(272, 386, 130, 32);
        _usersBtn.Click += (_, _) => { if (_api is not null) new UsersForm(_api).ShowDialog(this); };

        _vncLockBtn.SetBounds(412, 386, 170, 32);
        _vncLockBtn.Click += (_, _) => ToggleLocalVncLock();

        // Az admin-gombok login után, csak adminnak láthatók.
        foreach (var b in new[] { _editBtn, _approveBtn, _bootstrapBtn, _channelsBtn, _usersBtn, _vncLockBtn })
            b.Visible = false;

        _status.SetBounds(12, 428, 940, 30);
        _status.Text = "Indulás…";

        Controls.AddRange([_list, _refreshBtn, _connectBtn, _editBtn, _approveBtn, _bootstrapBtn, _channelsBtn, _usersBtn, _vncLockBtn, _status]);

        Load += async (_, _) => await InitAsync();
        FormClosing += (_, _) => Cleanup();
    }

    private async Task InitAsync()
    {
        try
        {
            // A konzol CSAK beléptetett gépen működik: a helyi agent brókerén át éri el a
            // szervert (a gép SSH-kulcsával). Nincs agent → nincs konzol.
            SetStatus("Helyi agent keresése…");
            _broker = BrokerClient.TryConnect();
            if (_broker is null)
            {
                MessageBox.Show(
                    "Ezen a gépen nem fut a RemoteAgent (offline), ezért a konzol nem használható.\n\n" +
                    "Telepítsd (újra) az agentet, majd indítsd újra a klienst.",
                    "Nincs agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus("Offline — nincs helyi agent. Telepítsd újra.");
                return;
            }

            SetStatus("Admin-csatorna a helyi agenten át…");
            var adminPort = await _broker.ForwardAsync(_cfg.AdminApiPort);
            _api = new AdminApi($"http://127.0.0.1:{adminPort}");

            SetStatus("Bejelentkezés…");
            using (var login = new LoginForm(_api))
            {
                if (login.ShowDialog(this) != DialogResult.OK)
                {
                    SetStatus("Bejelentkezés megszakítva.");
                    BeginInvoke(Close);
                    return;
                }
                _role = login.Role;
            }

            // Admin-gombok csak adminnak (operator csak listáz + csatlakozik).
            if (_role == "admin")
            {
                foreach (var b in new[] { _editBtn, _approveBtn, _bootstrapBtn, _channelsBtn, _usersBtn, _vncLockBtn })
                    b.Visible = true;
                UpdateVncLockBtn();
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Hiba: " + ex.Message);
        }
    }

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
                var item = new ListViewItem(string.IsNullOrEmpty(d.Hostname) ? "(névtelen)" : d.Hostname)
                {
                    Tag = d,
                };
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
                if (!string.IsNullOrWhiteSpace(d.LastIncident))
                    item.ToolTipText = "Supervisor: " + d.LastIncident;
                _list.Items.Add(item);
            }
            SetStatus($"{devices.Count} eszköz. Válassz egyet és Csatlakozás.");
        }
        catch (Exception ex)
        {
            SetStatus("Lista hiba: " + ex.Message);
        }
    }

    /// <summary>Rövid verzió-megjelenítés a listához (üres/null → „—").</summary>
    private static string Short(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v;

    private void UpdateVncLockBtn() =>
        _vncLockBtn.Text = LocalVncLock.IsLocked() ? "Helyi zár FELOLDÁSA" : "Helyi gép zárolása";

    /// <summary>A HELYI gép VNC-zárának ki/be kapcsolása (admin; emelt joggal, UAC).</summary>
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
                SetStatus(locked ? "Helyi gép feloldva — távolról ismét elérhető." : "Helyi gép ZÁROLVA — távolról nem elérhető.");
                UpdateVncLockBtn();
            }
            else SetStatus("A művelet nem fejeződött be (UAC megszakítva?).");
        }
        catch (Exception ex) { SetStatus("Helyi zár hiba: " + ex.Message); }
    }

    private async Task ConnectSelectedAsync()
    {
        if (_api is null || _list.SelectedItems.Count == 0) return;
        var sel = (DeviceInfo)_list.SelectedItems[0].Tag!;

        try
        {
            _connectBtn.Enabled = false;

            // FRISS adatok: a jelszó rotálódhatott a lista frissítése óta — mindig a
            // legutóbbi vnc_secret-et és online-állapotot használjuk a csatlakozáshoz.
            SetStatus("Friss adatok lekérése…");
            var devices = await _api.GetDevicesAsync();
            var d = devices.FirstOrDefault(x => x.DeviceId == sel.DeviceId) ?? sel;

            if (!d.Online)
            {
                MessageBox.Show("A gép nincs online — a tunnel csak akkor épül ki, ha csatlakozott.",
                    "Offline", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrEmpty(d.VncSecret))
            {
                MessageBox.Show("Nincs VNC-jelszó a géphez (még nem jelentette). Próbáld később.",
                    "Nincs jelszó", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetStatus($"Tunnel nyitása: {d.Hostname}…");
            var result = await _api.OpenTunnelAsync(d.DeviceId);
            if (result is null) { SetStatus("A tunnel-kérés sikertelen."); return; }

            SetStatus("Bástya-port elérése a helyi agenten át…");
            await Task.Delay(1500); // a cél gép reverse tunnelje felálljon
            var localPort = await _broker!.ForwardAsync(result.RemotePort);

            LaunchViewer(localPort, d.VncSecret!);
            SetStatus($"VNC indítva: {d.Hostname}");
        }
        catch (Exception ex)
        {
            SetStatus("Csatlakozási hiba: " + ex.Message);
        }
        finally
        {
            _connectBtn.Enabled = true;
        }
    }

    private async Task ApproveSelectedAsync()
    {
        if (_api is null || _list.SelectedItems.Count == 0) return;
        var sel = (DeviceInfo)_list.SelectedItems[0].Tag!;
        if (string.Equals(sel.Status, "Approved", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus($"{sel.Hostname} már jóváhagyott.");
            return;
        }
        if (MessageBox.Show($"Jóváhagyod ezt a gépet?\n\n{sel.Hostname}\n{sel.DeviceId}", "Jóváhagyás",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            await _api.ApproveDeviceAsync(sel.DeviceId);
            SetStatus($"{sel.Hostname} jóváhagyva.");
            await RefreshAsync();
        }
        catch (Exception ex) { SetStatus("Jóváhagyás hiba: " + ex.Message); }
    }

    private async Task GenerateBootstrapAsync()
    {
        if (_api is null) return;
        try
        {
            var blob = await _api.CreateBootstrapAsync(maxUses: 100000, expiresInHours: null);
            if (string.IsNullOrWhiteSpace(blob)) { SetStatus("Bootstrap: üres válasz a szervertől."); return; }
            try { Clipboard.SetText(blob); } catch { /* vágólap néha foglalt */ }
            MessageBox.Show(
                "Bootstrap blob (vágólapra másolva):\n\n" + blob +
                "\n\nTelepítés az ügyfélnél (admin):\n" +
                "  RemoteAgent.exe bootstrap <blob>\n" +
                "  RemoteAgent.exe install-service\n\n" +
                "A gép Pending-be kerül — itt hagyd jóvá a „Jóváhagyás\" gombbal.",
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
        catch (Exception ex)
        {
            SetStatus("Szerkesztés hiba: " + ex.Message);
        }
    }

    private void LaunchViewer(int localPort, string password)
    {
        var psi = new ProcessStartInfo(_cfg.ViewerExe) { UseShellExecute = false };
        psi.ArgumentList.Add("-host=127.0.0.1");
        psi.ArgumentList.Add($"-port={localPort}");
        psi.ArgumentList.Add($"-password={password}");
        // A forwardot a bróker tartja a session végéig; a viewer kilépésekor nem kell bontani.
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

    private void Cleanup()
    {
        try { _api?.LogoutAsync().Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort: session visszavonás */ }
        _broker?.Dispose(); // a kapcsolat bontásával az agent lebontja az összes forwardot
        _api?.Dispose();
    }
}
