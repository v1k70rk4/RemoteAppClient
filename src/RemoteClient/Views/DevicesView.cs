using System.Diagnostics;
using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// Device list plus in-window tabbed editor, like Users:
/// "Back | General | Log | Telemetry". Connect/Approve remain on the list.
/// </summary>
public sealed class DevicesView : UserControl, IContentView
{
    private readonly AdminApi _api;
    private readonly BrokerClient _broker;
    private readonly ClientConfig _cfg;
    private readonly bool _isAdmin;

    private readonly List<DeviceInfo> _devices = new();
    private readonly Panel _listHost = new() { Dock = DockStyle.Fill };
    private readonly Panel _editorHost = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly ListView _list = new();
    private readonly MaterialLabel _status = new();
    private readonly MaterialTextBox2 _search = new() { Hint = L.DevicesView_SearchHostnameOrNote, Width = 360 };
    private readonly MaterialButton _connectBtn = new() { Text = L.DevicesView_Connect, AutoSize = true };

    // Editor
    private readonly MaterialButton _tabGeneral = TabBtn(L.ChannelsView_General);
    private readonly MaterialButton _tabLog = TabBtn("LOG");
    private readonly MaterialButton _tabTelemetry = TabBtn(L.DevicesView_Telemetry);
    private readonly MaterialLabel _editorTitle = new() { Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Margin = new Padding(12, 10, 0, 0) };
    private readonly Panel _tabContent = new() { Dock = DockStyle.Fill };
    private DeviceInfo? _editing;
    private int _sortColumn = -1;
    private bool _sortAsc = true;
    private DeviceGeneralPanel? _generalPanel;
    private LogPanel? _logPanel;
    private DeviceTelemetryPanel? _telemetryPanel;

    public DevicesView(AdminApi api, BrokerClient broker, ClientConfig cfg, bool isAdmin)
    {
        _api = api; _broker = broker; _cfg = cfg; _isAdmin = isAdmin;
        Dock = DockStyle.Fill;
        BuildList();
        BuildEditor();
        Controls.Add(_editorHost);
        Controls.Add(_listHost);
        ApplyTheme();
    }

    private static MaterialButton TabBtn(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(4, 0, 0, 0), Type = MaterialButton.MaterialButtonType.Text };

    private void BuildList()
    {
        // Top bar: list-level actions (search + refresh).
        var tools = ViewUi.Toolbar();
        _search.Margin = new Padding(4, 0, 16, 0);
        _search.TextChanged += (_, _) => RenderList();
        tools.Controls.Add(_search);
        var refresh = ViewUi.ToolbarButton(L.AboutView_Refresh, primary: false);
        refresh.Click += async (_, _) => await RefreshAsync();
        tools.Controls.Add(refresh);

        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false;
        _list.BorderStyle = BorderStyle.None; _list.ShowItemToolTips = true;
        _list.Columns.Add(L.DevicesView_Device, 160);
        _list.Columns.Add(L.BootstrapView_Group, 110);
        _list.Columns.Add(L.DeviceGeneralPanel_Note, 160);
        _list.Columns.Add("Online", 70);
        _list.Columns.Add(L.CredentialDialog_User, 150);
        _list.Columns.Add(L.DevicesView_LastOnline, 140);
        _list.Columns.Add(L.DeviceTelemetryPanel_PublicIP, 120);
        _list.DoubleClick += async (_, _) => await ConnectSelectedAsync();
        _list.ColumnClick += (_, e) => { if (_sortColumn == e.Column) _sortAsc = !_sortAsc; else { _sortColumn = e.Column; _sortAsc = true; } RenderList(); };

        // Bottom-right: actions for the selected device (Connect | Edit | Approve).
        // With RightToLeft, the first added control is rightmost, so add in reverse order.
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6, 4, 8, 6) };
        void RightBtn(string text, EventHandler onClick) { var b = ViewUi.ToolbarButton(text); b.Margin = new Padding(4, 0, 4, 0); b.Click += onClick; actions.Controls.Add(b); }
        if (_isAdmin) RightBtn(L.DevicesView_UnlockSignIn, async (_, _) => await UnlockSelectedAsync());
        if (_isAdmin) RightBtn(L.DevicesView_Approve, async (_, _) => await ApproveSelectedAsync());
        if (_isAdmin) RightBtn(L.DevicesView_Telemetry, async (_, _) => await EditSelectedAsync("telemetry"));
        if (_isAdmin) RightBtn(L.DevicesView_Properties, async (_, _) => await EditSelectedAsync());
        _connectBtn.Margin = new Padding(4, 0, 4, 0);
        _connectBtn.Click += async (_, _) => await ConnectSelectedAsync();
        actions.Controls.Add(_connectBtn); // added last -> leftmost (Connect)

        _listHost.Controls.Add(ViewUi.Rows(1, tools, _list, actions, ViewUi.StatusHost(_status)));
    }

    private void BuildEditor()
    {
        var back = ViewUi.ToolbarButton(L.ChannelsView_Back, primary: false);
        back.Click += async (_, _) => { ShowList(); await RefreshAsync(); };
        _tabGeneral.Click += (_, _) => SelectTab("general");
        _tabLog.Click += async (_, _) => await SelectTabAsync("log");
        _tabTelemetry.Click += (_, _) => SelectTab("telemetry");

        var tabbar = ViewUi.Toolbar();
        tabbar.Controls.AddRange([back, _tabGeneral, _tabLog, _tabTelemetry]);

        _editorHost.Controls.Add(ViewUi.Rows(2, tabbar, _editorTitle, _tabContent));
    }

    public void ApplyTheme() => ThemeManager.StyleView(this, _list);

    public async Task OnShownAsync() { ShowList(); await RefreshAsync(); }

    private void ShowList() { _editorHost.Visible = false; _listHost.Visible = true; _listHost.BringToFront(); }
    private void ShowEditor() { _listHost.Visible = false; _editorHost.Visible = true; _editorHost.BringToFront(); }

    private DeviceInfo? SelectedDevice() => _list.SelectedItems.Count == 0 ? null : (DeviceInfo)_list.SelectedItems[0].Tag!;

    private async Task RefreshAsync()
    {
        try
        {
            SetStatus(L.DevicesView_FetchingDeviceList);
            var devices = await RetryAsync(() => _api.GetDevicesAsync());
            _devices.Clear(); _devices.AddRange(devices);
            RenderList();
            SetStatus(L.Format(L.DevicesView_Device_2, devices.Count));
        }
        catch (Exception ex) { SetStatus(L.DevicesView_ListError + ex.Message); }
    }

    private void RenderList()
    {
        var q = _search.Text.Trim();
        IEnumerable<DeviceInfo> items = _devices;
        if (q.Length > 0)
            items = _devices.Where(d =>
                (d.Hostname?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.Note?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));

        items = SortItems(items);

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var d in items)
        {
            // Column order: Device | Group | Note | Online | User | Last seen | Public IP.
            var name = string.IsNullOrEmpty(d.Hostname) ? L.DevicesView_Unnamed : d.Hostname;
            if (d.LoginLocked) name = "🔒 " + name;
            var item = new ListViewItem(name) { Tag = d, UseItemStyleForSubItems = false };
            if (d.LoginLocked) item.ToolTipText = L.Format(L.DevicesView_SignInLockedFailedAttempts, d.LoginFailCount);
            item.SubItems.Add(d.GroupName ?? "—");
            item.SubItems.Add(string.IsNullOrWhiteSpace(d.Note) ? "—" : d.Note);
            var online = item.SubItems.Add(d.Online ? "● online" : "○ offline");
            online.ForeColor = d.Online ? Color.MediumSeaGreen : Color.Gray;
            item.SubItems.Add(string.IsNullOrWhiteSpace(d.LoggedInUser) ? "—" : d.LoggedInUser);
            item.SubItems.Add(d.LastSeenAt?.LocalDateTime.ToString("g") ?? "—");
            item.SubItems.Add(string.IsNullOrWhiteSpace(d.PublicIpAddress) ? "—" : d.PublicIpAddress);
            if (!string.IsNullOrWhiteSpace(d.LastIncident)) item.ToolTipText = "Supervisor: " + d.LastIncident;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }

    // Sort by clicked column with type-aware handling for date/online.
    private IEnumerable<DeviceInfo> SortItems(IEnumerable<DeviceInfo> items)
    {
        if (_sortColumn < 0) return items;
        Func<DeviceInfo, object?> key = _sortColumn switch
        {
            0 => d => d.Hostname,
            1 => d => d.GroupName,
            2 => d => d.Note,
            3 => d => d.Online,
            4 => d => d.LoggedInUser,
            5 => d => d.LastSeenAt,
            6 => d => d.PublicIpAddress,
            _ => d => d.Hostname,
        };
        return _sortAsc
            ? items.OrderBy(key, Comparer<object?>.Create(CompareKeys))
            : items.OrderByDescending(key, Comparer<object?>.Create(CompareKeys));
    }

    private static int CompareKeys(object? a, object? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        if (a is IComparable c && a.GetType() == b.GetType()) return c.CompareTo(b);
        return string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task EditSelectedAsync(string initialTab = "general")
    {
        if (SelectedDevice() is not { } d) { SetStatus(L.DevicesView_SelectADevice); return; }
        try
        {
            var groups = await _api.GetGroupsAsync();
            _editing = d;
            _editorTitle.Text = string.IsNullOrEmpty(d.Hostname) ? d.DeviceId : d.Hostname;

            _generalPanel?.Dispose(); _logPanel?.Dispose(); _telemetryPanel?.Dispose();
            _generalPanel = new DeviceGeneralPanel(_api, d, groups);
            _logPanel = new LogPanel(_api, deviceId: d.DeviceId);
            _telemetryPanel = new DeviceTelemetryPanel(d);

            ShowEditor();
            SelectTab(initialTab);
        }
        catch (Exception ex) { SetStatus(L.DevicesView_PropertiesError + ex.Message); }
    }

    private void SelectTab(string tab) => _ = SelectTabAsync(tab);

    private async Task SelectTabAsync(string tab)
    {
        foreach (var (b, key) in new[] { (_tabGeneral, "general"), (_tabLog, "log"), (_tabTelemetry, "telemetry") })
            b.Type = key == tab ? MaterialButton.MaterialButtonType.Contained : MaterialButton.MaterialButtonType.Text;

        _tabContent.Controls.Clear();
        switch (tab)
        {
            case "general" when _generalPanel is not null: _tabContent.Controls.Add(_generalPanel); break;
            case "telemetry" when _telemetryPanel is not null: _tabContent.Controls.Add(_telemetryPanel); break;
            case "log" when _logPanel is not null: _tabContent.Controls.Add(_logPanel); await _logPanel.ShownAsync(); break;
        }
    }

    /// <summary>
    /// Waits for access outcome. First waits quietly for about 2.5s so auto/unattended access
    /// does not flash a dialog; if still pending, shows a wait dialog for the remaining time.
    /// </summary>
    private async Task<string> WaitAccessAsync(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce)) return "auto";
        for (int i = 0; i < 4; i++)
        {
            try { var o = await _api.GetAccessResultAsync(nonce); if (!string.IsNullOrEmpty(o)) return o; }
            catch { /* tranziens */ }
            await Task.Delay(600);
        }
        using var w = new ConsentWaitForm(_api, nonce);
        w.ShowDialog(this);
        return w.Outcome;
    }

    private async Task ConnectSelectedAsync()
    {
        if (SelectedDevice() is not { } sel) { SetStatus(L.DevicesView_SelectADevice); return; }
        try
        {
            _connectBtn.Enabled = false;
            SetStatus(L.DevicesView_FetchingLatestData);
            var devices = await _api.GetDevicesAsync();
            var d = devices.FirstOrDefault(x => x.DeviceId == sel.DeviceId) ?? sel;

            if (!d.Online) { MessageBox.Show(L.DevicesView_TheDeviceIsOffline, "Offline", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (string.IsNullOrEmpty(d.VncSecret)) { MessageBox.Show(L.DevicesView_NoVNCPasswordForThis, L.DevicesView_NoPassword, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            SetStatus(L.Format(L.DevicesView_OpeningTunnel, d.Hostname));
            var result = await _api.OpenTunnelAsync(d.DeviceId);
            if (result is null) { SetStatus(L.DevicesView_TunnelRequestFailed); return; }

            SetStatus(L.DevicesView_WaitingForTheRemoteDevice);
            var outcome = await WaitAccessAsync(result.Nonce);
            if (outcome is not ("auto" or "granted"))
            {
                var (title, text) = outcome switch
                {
                    "denied"    => (L.DevicesView_Denied, L.DevicesView_TheUserAtTheDevice),
                    "timeout"   => (L.DevicesView_NoResponse, L.DevicesView_TheUserDidNotRespond),
                    "no-user"   => (L.DevicesView_NoUser, L.DevicesView_NoOneIsSignedIn),
                    "locked"    => (L.DevicesView_Disabled, L.DevicesView_RemoteAccessIsLocallyDisabled),
                    "cancelled" => (L.DevicesView_Cancelled, ""),
                    _           => (L.DevicesView_Failed, L.DevicesView_TheConnectionWasNotEstablished),
                };
                SetStatus(title);
                if (!string.IsNullOrEmpty(text))
                    MessageBox.Show(text, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetStatus(L.DevicesView_ReachingBastionPortThroughThe);
            await Task.Delay(1500);
            var localPort = await _broker.ForwardAsync(result.RemotePort);
            LaunchViewer(localPort, d.VncSecret!);
            SetStatus(L.Format(L.DevicesView_VNCStarted, d.Hostname));
        }
        catch (Exception ex) { SetStatus(L.DevicesView_ConnectionError + ex.Message); }
        finally { _connectBtn.Enabled = true; }
    }

    private async Task UnlockSelectedAsync()
    {
        if (SelectedDevice() is not { } sel) { SetStatus(L.DevicesView_SelectADevice); return; }
        if (!sel.LoginLocked) { SetStatus(L.Format(L.DevicesView_IsNotSignInLocked, sel.Hostname)); return; }
        if (MessageBox.Show(L.Format(L.DevicesView_UnlockSignInOnThis, sel.Hostname), L.DevicesView_UnlockSignIn, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { await _api.UnlockDeviceAsync(sel.DeviceId); SetStatus(L.Format(L.DevicesView_SignInLockCleared, sel.Hostname)); await RefreshAsync(); }
        catch (Exception ex) { SetStatus(L.DevicesView_UnlockError + ex.Message); }
    }

    private async Task ApproveSelectedAsync()
    {
        if (SelectedDevice() is not { } sel) return;
        if (string.Equals(sel.Status, "Approved", StringComparison.OrdinalIgnoreCase)) { SetStatus(L.Format(L.DevicesView_IsAlreadyApproved, sel.Hostname)); return; }
        if (MessageBox.Show(L.Format(L.DevicesView_ApproveThisDevice, sel.Hostname, sel.DeviceId), L.DevicesView_Approve, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { await _api.ApproveDeviceAsync(sel.DeviceId); SetStatus(L.Format(L.DevicesView_Approved, sel.Hostname)); await RefreshAsync(); }
        catch (Exception ex) { SetStatus(L.DevicesView_ApproveError + ex.Message); }
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
