using System.Diagnostics;
using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient.Views;

/// <summary>Eszközlista + műveletek (csatlakozás, szerkesztés, jóváhagyás). Egy ablakon belül, a fő nézet tartalomterületén.</summary>
public sealed class DevicesView : UserControl, IContentView
{
    private readonly AdminApi _api;
    private readonly BrokerClient _broker;
    private readonly ClientConfig _cfg;
    private readonly bool _isAdmin;

    private readonly ListView _list = new();
    private readonly MaterialLabel _status = new();
    private readonly MaterialButton _refreshBtn = new() { Text = "Frissítés", AutoSize = true };
    private readonly MaterialButton _connectBtn = new() { Text = "Csatlakozás", AutoSize = true };
    private readonly MaterialButton _editBtn = new() { Text = "Szerkesztés", AutoSize = true };
    private readonly MaterialButton _approveBtn = new() { Text = "Jóváhagyás", AutoSize = true };

    public DevicesView(AdminApi api, BrokerClient broker, ClientConfig cfg, bool isAdmin)
    {
        _api = api; _broker = broker; _cfg = cfg; _isAdmin = isAdmin;
        Dock = DockStyle.Fill;

        var tools = ViewUi.Toolbar();
        void add(MaterialButton b, EventHandler onClick) { b.Margin = new Padding(4, 0, 4, 0); b.Click += onClick; tools.Controls.Add(b); }
        add(_refreshBtn, async (_, _) => await RefreshAsync());
        add(_connectBtn, async (_, _) => await ConnectSelectedAsync());
        if (_isAdmin)
        {
            add(_editBtn, async (_, _) => await EditSelectedAsync());
            add(_approveBtn, async (_, _) => await ApproveSelectedAsync());
        }

        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false;
        _list.BorderStyle = BorderStyle.None; _list.ShowItemToolTips = true;
        _list.Columns.Add("Gép", 180); _list.Columns.Add("Állapot", 90); _list.Columns.Add("Online", 60);
        _list.Columns.Add("Csoport", 110); _list.Columns.Add("Update", 60); _list.Columns.Add("Csat.", 55);
        _list.Columns.Add("Zár", 45); _list.Columns.Add("Verzió A/H/V", 130); _list.Columns.Add("Restart", 55);
        _list.Columns.Add("Utoljára látva", 130); _list.Columns.Add("deviceId", 170);
        _list.DoubleClick += async (_, _) => await ConnectSelectedAsync();

        Controls.Add(ViewUi.Rows(1, tools, _list, ViewUi.StatusHost(_status)));
        ApplyTheme();
    }

    public void ApplyTheme() => ThemeManager.StyleView(this, _list);

    /// <summary>Aktiváláskor (nézetváltás) friss lista.</summary>
    public async Task OnShownAsync() => await RefreshAsync();

    private async Task RefreshAsync()
    {
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

    private async Task ConnectSelectedAsync()
    {
        if (_list.SelectedItems.Count == 0) { SetStatus("Válassz egy gépet."); return; }
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
            var localPort = await _broker.ForwardAsync(result.RemotePort);
            LaunchViewer(localPort, d.VncSecret!);
            SetStatus($"VNC indítva: {d.Hostname}");
        }
        catch (Exception ex) { SetStatus("Csatlakozási hiba: " + ex.Message); }
        finally { _connectBtn.Enabled = true; }
    }

    private async Task ApproveSelectedAsync()
    {
        if (_list.SelectedItems.Count == 0) return;
        var sel = (DeviceInfo)_list.SelectedItems[0].Tag!;
        if (string.Equals(sel.Status, "Approved", StringComparison.OrdinalIgnoreCase)) { SetStatus($"{sel.Hostname} már jóváhagyott."); return; }
        if (MessageBox.Show($"Jóváhagyod ezt a gépet?\n\n{sel.Hostname}\n{sel.DeviceId}", "Jóváhagyás", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { await _api.ApproveDeviceAsync(sel.DeviceId); SetStatus($"{sel.Hostname} jóváhagyva."); await RefreshAsync(); }
        catch (Exception ex) { SetStatus("Jóváhagyás hiba: " + ex.Message); }
    }

    private async Task EditSelectedAsync()
    {
        if (_list.SelectedItems.Count == 0) return;
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
}
