using System.Diagnostics;
using System.Drawing;
using MaterialSkin;
using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient.Views;

/// <summary>Eszközlista (keresés + lényegi oszlopok) + jobb oldali részletek-panel + műveletek.</summary>
public sealed class DevicesView : UserControl, IContentView
{
    private readonly AdminApi _api;
    private readonly BrokerClient _broker;
    private readonly ClientConfig _cfg;
    private readonly bool _isAdmin;

    private readonly List<DeviceInfo> _devices = new();
    private readonly ListView _list = new();
    private readonly MaterialLabel _status = new();
    private readonly MaterialTextBox2 _search = new() { Hint = "Keresés: gépnév vagy megjegyzés", Width = 240 };
    private readonly MaterialButton _refreshBtn = new() { Text = "Frissítés", AutoSize = true };
    private readonly MaterialButton _connectBtn = new() { Text = "Csatlakozás", AutoSize = true };
    private readonly MaterialButton _editBtn = new() { Text = "Szerkesztés", AutoSize = true };
    private readonly MaterialButton _approveBtn = new() { Text = "Jóváhagyás", AutoSize = true };

    // Részletek-panel (jobb oldal) — bővíthető a későbbi telemetriához.
    private readonly FlowLayoutPanel _detailsFlow = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 8, 8, 8) };

    public DevicesView(AdminApi api, BrokerClient broker, ClientConfig cfg, bool isAdmin)
    {
        _api = api; _broker = broker; _cfg = cfg; _isAdmin = isAdmin;
        Dock = DockStyle.Fill;

        var tools = ViewUi.Toolbar();
        _search.Margin = new Padding(4, 0, 16, 0);
        _search.TextChanged += (_, _) => RenderList();
        tools.Controls.Add(_search);
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
        _list.Columns.Add("Gép", 180);
        _list.Columns.Add("Megjegyzés", 220);
        _list.Columns.Add("Csoport", 130);
        _list.Columns.Add("Online", 70);
        _list.Columns.Add("Utoljára online", 150);
        _list.SelectedIndexChanged += (_, _) => RenderDetails(SelectedDevice());
        _list.DoubleClick += async (_, _) => await ConnectSelectedAsync();

        // Középső terület: lista (kitölt) + részletek-panel (jobb szél).
        var center = new Panel { Dock = DockStyle.Fill };
        var detailsCard = new MaterialCard { Dock = DockStyle.Right, Width = 320, Margin = new Padding(0), Padding = new Padding(0) };
        var detailsHead = new MaterialLabel { Text = "Részletek", Font = new Font("Segoe UI", 11F, FontStyle.Bold), Dock = DockStyle.Top, Height = 30, Padding = new Padding(12, 6, 0, 0) };
        detailsCard.Controls.Add(_detailsFlow);
        detailsCard.Controls.Add(detailsHead);
        // A Fill-t (lista) ELŐBB adjuk hozzá, a jobb-dokkolt panelt utána (z-sorrend).
        _list.Dock = DockStyle.Fill;
        center.Controls.Add(_list);
        center.Controls.Add(detailsCard);

        Controls.Add(ViewUi.Rows(1, tools, center, ViewUi.StatusHost(_status)));
        ApplyTheme();
        RenderDetails(null);
    }

    public void ApplyTheme() => ThemeManager.StyleView(this, _list);

    public async Task OnShownAsync() => await RefreshAsync();

    private DeviceInfo? SelectedDevice() => _list.SelectedItems.Count == 0 ? null : (DeviceInfo)_list.SelectedItems[0].Tag!;

    private async Task RefreshAsync()
    {
        try
        {
            SetStatus("Eszközlista lekérése…");
            var devices = await RetryAsync(() => _api.GetDevicesAsync());
            _devices.Clear(); _devices.AddRange(devices);
            RenderList();
            SetStatus($"{devices.Count} eszköz.");
        }
        catch (Exception ex) { SetStatus("Lista hiba: " + ex.Message); }
    }

    private void RenderList()
    {
        var q = _search.Text.Trim();
        IEnumerable<DeviceInfo> items = _devices;
        if (q.Length > 0)
            items = _devices.Where(d =>
                (d.Hostname?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.Note?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var d in items)
        {
            var item = new ListViewItem(string.IsNullOrEmpty(d.Hostname) ? "(névtelen)" : d.Hostname) { Tag = d, UseItemStyleForSubItems = false };
            item.SubItems.Add(string.IsNullOrWhiteSpace(d.Note) ? "—" : d.Note);
            item.SubItems.Add(d.GroupName ?? "—");
            var online = item.SubItems.Add(d.Online ? "● online" : "○ offline");
            online.ForeColor = d.Online ? Color.MediumSeaGreen : Color.Gray;
            item.SubItems.Add(d.LastSeenAt?.LocalDateTime.ToString("g") ?? "—");
            if (!string.IsNullOrWhiteSpace(d.LastIncident)) item.ToolTipText = "Supervisor: " + d.LastIncident;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }

    private void RenderDetails(DeviceInfo? d)
    {
        _detailsFlow.SuspendLayout();
        _detailsFlow.Controls.Clear();
        if (d is null)
        {
            _detailsFlow.Controls.Add(new MaterialLabel { Text = "Válassz egy gépet a részletekhez.", AutoSize = true, Margin = new Padding(0, 4, 0, 0) });
            _detailsFlow.ResumeLayout();
            return;
        }

        void Row(string caption, string? value)
        {
            var cap = new MaterialLabel { Text = caption, AutoSize = true, FontType = MaterialSkinManager.fontType.Caption, Margin = new Padding(0, 8, 0, 0) };
            var val = new MaterialLabel { Text = string.IsNullOrWhiteSpace(value) ? "—" : value, AutoSize = true, MaximumSize = new Size(296, 0), Margin = new Padding(0, 0, 0, 0) };
            _detailsFlow.Controls.Add(cap);
            _detailsFlow.Controls.Add(val);
        }

        Row("Gép", d.Hostname);
        Row("Megjegyzés", d.Note);
        Row("Csoport", d.GroupName);
        Row("Online", d.Online ? "online" : "offline");
        Row("Utoljára online", d.LastSeenAt?.LocalDateTime.ToString("g"));
        Row("Állapot", d.Status);
        Row("Csatorna", string.Equals(d.Channel, "beta", StringComparison.OrdinalIgnoreCase) ? "BETA" : "rtm");
        Row("Frissíthető", d.UpdateAllowed ? "igen" : "NEM");
        Row("Unattended", d.UnattendedAllowed switch { true => "igen", false => "nem", null => "örökli" });
        Row("Consent kell", d.ConsentRequired switch { true => "igen", false => "nem", null => "örökli" });
        Row("Helyi zár", d.VncLocked ? "LETILTVA" : "—");
        Row("Verziók (A/H/V)", $"{Short(d.AgentVersion)} / {Short(d.HelperVersion)} / {Short(d.VncVersion)}");
        Row("Kliens / OS", $"{Short(d.ClientVersion)} / {Short(d.OsVersion)}");
        Row("Agent-restartok", d.AgentRestarts.ToString());
        if (!string.IsNullOrWhiteSpace(d.LastIncident)) Row("Utolsó incidens", d.LastIncident);
        Row("deviceId", d.DeviceId);
        _detailsFlow.ResumeLayout();
    }

    private static string Short(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v;

    private async Task ConnectSelectedAsync()
    {
        if (SelectedDevice() is not { } sel) { SetStatus("Válassz egy gépet."); return; }
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
        if (SelectedDevice() is not { } sel) return;
        if (string.Equals(sel.Status, "Approved", StringComparison.OrdinalIgnoreCase)) { SetStatus($"{sel.Hostname} már jóváhagyott."); return; }
        if (MessageBox.Show($"Jóváhagyod ezt a gépet?\n\n{sel.Hostname}\n{sel.DeviceId}", "Jóváhagyás", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { await _api.ApproveDeviceAsync(sel.DeviceId); SetStatus($"{sel.Hostname} jóváhagyva."); await RefreshAsync(); }
        catch (Exception ex) { SetStatus("Jóváhagyás hiba: " + ex.Message); }
    }

    private async Task EditSelectedAsync()
    {
        if (SelectedDevice() is not { } d) return;
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
