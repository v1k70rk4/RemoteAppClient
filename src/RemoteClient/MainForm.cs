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
    private SshForward? _adminForward;
    private AdminApi? _api;
    private readonly List<SshForward> _vncForwards = [];

    private readonly ListView _list = new();
    private readonly Button _refreshBtn = new();
    private readonly Button _connectBtn = new();
    private readonly Button _editBtn = new();
    private readonly Label _status = new();

    public MainForm()
    {
        _cfg = ClientConfig.Load();

        Text = "RemoteAppClient — admin konzol";
        Width = 760;
        Height = 460;
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
        _list.Columns.Add("Utoljára látva", 130);
        _list.Columns.Add("deviceId", 170);
        _list.DoubleClick += async (_, _) => await ConnectSelectedAsync();

        _refreshBtn.Text = "Frissítés";
        _refreshBtn.SetBounds(12, 350, 110, 32);
        _refreshBtn.Click += async (_, _) => await RefreshAsync();

        _connectBtn.Text = "Csatlakozás (VNC)";
        _connectBtn.SetBounds(132, 350, 160, 32);
        _connectBtn.Click += async (_, _) => await ConnectSelectedAsync();

        _editBtn.Text = "Szerkesztés…";
        _editBtn.SetBounds(302, 350, 130, 32);
        _editBtn.Click += async (_, _) => await EditSelectedAsync();

        _status.SetBounds(12, 392, 720, 30);
        _status.Text = "Indulás…";

        Controls.AddRange([_list, _refreshBtn, _connectBtn, _editBtn, _status]);

        Load += async (_, _) => await InitAsync();
        FormClosing += (_, _) => Cleanup();
    }

    private async Task InitAsync()
    {
        if (!_cfg.IsComplete)
        {
            _cfg.Save(); // sablon kiírása
            MessageBox.Show(
                $"Töltsd ki a beállításokat (SSH host/user/kulcs):\n{ClientConfig.Path}\n\nUtána indítsd újra.",
                "Beállítás szükséges", MessageBoxButtons.OK, MessageBoxIcon.Information);
            SetStatus("Hiányzó beállítás — lásd config.json.");
            return;
        }

        try
        {
            SetStatus("Admin-alagút építése a boxra…");
            _adminForward = new SshForward(_cfg, _cfg.AdminApiPort);
            _api = new AdminApi($"http://127.0.0.1:{_adminForward.LocalPort}");
            await Task.Delay(1500); // az ssh -L felállása
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
                item.SubItems.Add(d.LastSeenAt?.LocalDateTime.ToString("g") ?? "—");
                item.SubItems.Add(d.DeviceId);
                _list.Items.Add(item);
            }
            SetStatus($"{devices.Count} eszköz. Válassz egyet és Csatlakozás.");
        }
        catch (Exception ex)
        {
            SetStatus("Lista hiba: " + ex.Message);
        }
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

            SetStatus("Bástya-port elérése…");
            var vnc = new SshForward(_cfg, result.RemotePort);
            _vncForwards.Add(vnc);
            await Task.Delay(2500); // a reverse tunnel + az ssh -L felállása

            LaunchViewer(vnc.LocalPort, d.VncSecret!, vnc);
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

    private void LaunchViewer(int localPort, string password, SshForward forward)
    {
        var psi = new ProcessStartInfo(_cfg.ViewerExe)
        {
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-host=127.0.0.1");
        psi.ArgumentList.Add($"-port={localPort}");
        psi.ArgumentList.Add($"-password={password}");

        var viewer = Process.Start(psi);
        if (viewer is not null)
        {
            viewer.EnableRaisingEvents = true;
            viewer.Exited += (_, _) =>
            {
                forward.Dispose();
                _vncForwards.Remove(forward);
            };
        }
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
        foreach (var f in _vncForwards.ToArray()) f.Dispose();
        _adminForward?.Dispose();
        _api?.Dispose();
    }
}
