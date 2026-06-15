using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
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
    private string _viewerScale;   // TightVNC viewer scale: "auto" (fit to window) or a percent "1".."400"; per-operator, from the account
    private string _viewerColor;   // TightVNC color depth: "full" or "256" (8-bit, low-color); per-operator, from the account

    private readonly List<DeviceInfo> _devices = new();
    private readonly Panel _listHost = new() { Dock = DockStyle.Fill };
    private readonly Panel _editorHost = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly ListView _list = new();
    private readonly MaterialLabel _status = new();
    private readonly MaterialTextBox2 _search = new() { Hint = L.DevicesView_SearchHostnameOrNote, Width = 360 };
    private readonly MaterialButton _connectBtn = new() { Text = L.DevicesView_Connect, AutoSize = true };

    // Editor
    private readonly MaterialButton _tabGeneral = TabBtn(L.ChannelsView_General);
    private readonly MaterialButton _tabPermissions = TabBtn(L.UsersView_Permissions);
    private readonly MaterialButton _tabMessages = TabBtn(L.DevicesView_Messages);
    private readonly MaterialButton _tabCommands = TabBtn(L.DevicesView_Commands);
    private readonly MaterialButton _tabLog = TabBtn("LOG");
    private readonly MaterialButton _tabTelemetry = TabBtn(L.DevicesView_Telemetry);
    private readonly MaterialLabel _editorTitle = new() { Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Margin = new Padding(12, 10, 0, 0) };
    private readonly Panel _tabContent = new() { Dock = DockStyle.Fill };
    private DeviceInfo? _editing;
    private int _sortColumn = -1;
    private bool _sortAsc = true;
    private DeviceGeneralPanel? _generalPanel;
    private DevicePermissionsPanel? _permPanel;
    private DeviceMessagesPanel? _msgPanel;
    private DeviceCommandsPanel? _cmdPanel;
    private LogPanel? _logPanel;
    private DeviceTelemetryPanel? _telemetryPanel;

    public DevicesView(AdminApi api, BrokerClient broker, ClientConfig cfg, bool isAdmin, string viewerScale = "auto", string viewerColor = "full")
    {
        _api = api; _broker = broker; _cfg = cfg; _isAdmin = isAdmin;
        _viewerScale = string.IsNullOrWhiteSpace(viewerScale) ? "auto" : viewerScale;
        _viewerColor = string.IsNullOrWhiteSpace(viewerColor) ? "full" : viewerColor;
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
        _list.Columns.Add(L.DevicesView_Update, 150);
        _list.DoubleClick += async (_, _) => await ConnectSelectedAsync();
        // Right-click selects the row under the cursor and opens a context menu of editor tabs.
        if (_isAdmin)
        {
            var menu = new ContextMenuStrip();
            void Item(string text, string tab) => menu.Items.Add(text, null, async (_, _) => await EditSelectedAsync(tab));
            Item(L.DevicesView_Properties, "general");
            Item(L.DevicesView_Messages, "messages");
            Item("Log", "log");
            Item(L.DevicesView_Telemetry, "telemetry");
            Item(L.UsersView_Permissions, "permissions");

            // Power commands directly in the menu (each confirms first; cancel is a safe undo).
            menu.Items.Add(new ToolStripSeparator());
            var power = new ToolStripMenuItem(L.DevicesView_Commands);
            power.DropDownItems.Add(L.DeviceCommandsPanel_Restart, null, async (_, _) => await RunPowerAsync("restart", confirm: true));
            power.DropDownItems.Add(L.DeviceCommandsPanel_ForceRestart, null, async (_, _) => await RunPowerAsync("force-restart", confirm: true));
            power.DropDownItems.Add(L.DeviceCommandsPanel_CancelRestart, null, async (_, _) => await RunPowerAsync("cancel", confirm: false));
            power.DropDownItems.Add(L.DeviceCommandsPanel_Logout, null, async (_, _) => await RunPowerAsync("logout", confirm: true));
            menu.Items.Add(power);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L.DevicesView_Delete, null, async (_, _) => await DeleteSelectedAsync());

            _list.MouseUp += (_, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var hit = _list.GetItemAt(e.X, e.Y);
                if (hit is null) return;
                hit.Selected = true;
                menu.Show(_list, e.Location);
            };
        }
        _list.ColumnClick += (_, e) => { if (_sortColumn == e.Column) _sortAsc = !_sortAsc; else { _sortColumn = e.Column; _sortAsc = true; } RenderList(); };

        // Bottom-right: actions for the selected device (Connect | Edit | Approve).
        // With RightToLeft, the first added control is rightmost, so add in reverse order.
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6, 4, 8, 6) };
        void RightBtn(string text, EventHandler onClick) { var b = ViewUi.ToolbarButton(text); b.Margin = new Padding(4, 0, 4, 0); b.Click += onClick; actions.Controls.Add(b); }
        // Properties/Messages/Log/Telemetry/Permissions now live in the right-click menu on the list.
        if (_isAdmin) RightBtn(L.DevicesView_Delete, async (_, _) => await DeleteSelectedAsync());
        if (_isAdmin) RightBtn(L.DevicesView_UnlockSignIn, async (_, _) => await UnlockSelectedAsync());
        if (_isAdmin) RightBtn(L.DevicesView_Approve, async (_, _) => await ApproveSelectedAsync());
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
        _tabPermissions.Click += (_, _) => SelectTab("permissions");
        _tabMessages.Click += (_, _) => SelectTab("messages");
        _tabCommands.Click += (_, _) => SelectTab("commands");
        _tabLog.Click += async (_, _) => await SelectTabAsync("log");
        _tabTelemetry.Click += (_, _) => SelectTab("telemetry");

        var tabbar = ViewUi.Toolbar();
        tabbar.Controls.AddRange([back, _tabGeneral, _tabPermissions, _tabMessages, _tabCommands, _tabLog, _tabTelemetry]);

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
            // Column order: Device | Group | Note | Online | User | Last seen | Public IP | Update.
            var name = string.IsNullOrEmpty(d.Hostname) ? L.DevicesView_Unnamed : d.Hostname;
            if (d.LoginLocked) name = "🔒 " + name;
            var item = new ListViewItem(name) { Tag = d, UseItemStyleForSubItems = false };
            // Pending (not yet approved) must stand out: red device name.
            if (string.Equals(d.Status, "Pending", StringComparison.OrdinalIgnoreCase))
                item.ForeColor = Color.Red;
            if (d.LoginLocked) item.ToolTipText = L.Format(L.DevicesView_SignInLockedFailedAttempts, d.LoginFailCount);
            item.SubItems.Add(d.GroupName ?? "—");
            item.SubItems.Add(string.IsNullOrWhiteSpace(d.Note) ? "—" : d.Note);
            var online = item.SubItems.Add(d.Online ? "● online" : "○ offline");
            online.ForeColor = d.Online ? Color.MediumSeaGreen : Color.Gray;
            item.SubItems.Add(string.IsNullOrWhiteSpace(d.LoggedInUser) ? "—" : d.LoggedInUser);
            item.SubItems.Add(d.LastSeenAt?.LocalDateTime.ToString("g") ?? "—");
            item.SubItems.Add(string.IsNullOrWhiteSpace(d.PublicIpAddress) ? "—" : d.PublicIpAddress);
            // Rollout indicator: grey check + target while an update command is in flight; "—" once applied.
            var upd = item.SubItems.Add(d.UpdatePending ? "✓ " + (d.UpdatePendingInfo ?? "") : "—");
            if (d.UpdatePending) upd.ForeColor = Color.Gray;
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

            _generalPanel?.Dispose(); _permPanel?.Dispose(); _msgPanel?.Dispose(); _cmdPanel?.Dispose(); _logPanel?.Dispose(); _telemetryPanel?.Dispose();
            _generalPanel = new DeviceGeneralPanel(_api, d, groups);
            _permPanel = new DevicePermissionsPanel(_api, d);
            _msgPanel = new DeviceMessagesPanel(_api, d, () => ConnectDeviceAsync(d));
            _cmdPanel = new DeviceCommandsPanel(_api, d);
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
        foreach (var (b, key) in new[] { (_tabGeneral, "general"), (_tabPermissions, "permissions"), (_tabMessages, "messages"), (_tabCommands, "commands"), (_tabLog, "log"), (_tabTelemetry, "telemetry") })
            b.Type = key == tab ? MaterialButton.MaterialButtonType.Contained : MaterialButton.MaterialButtonType.Text;

        _tabContent.Controls.Clear();
        switch (tab)
        {
            case "general" when _generalPanel is not null: _tabContent.Controls.Add(_generalPanel); break;
            case "permissions" when _permPanel is not null: _tabContent.Controls.Add(_permPanel); break;
            case "messages" when _msgPanel is not null: _tabContent.Controls.Add(_msgPanel); break;
            case "commands" when _cmdPanel is not null: _tabContent.Controls.Add(_cmdPanel); break;
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
        await ConnectDeviceAsync(sel);
    }

    /// <summary>Opens the tunnel and launches the VNC viewer for a specific device (re-fetches latest state).</summary>
    private async Task ConnectDeviceAsync(DeviceInfo sel)
    {
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
            LaunchViewer(localPort, d);
            SetStatus(L.Format(L.DevicesView_VNCStarted, d.Hostname));
        }
        catch (Exception ex) { SetStatus(L.DevicesView_ConnectionError + ex.Message); }
        finally { _connectBtn.Enabled = true; }
    }

    /// <summary>Runs a power action on the selected device (right-click menu). Confirms first when asked.</summary>
    private async Task RunPowerAsync(string action, bool confirm)
    {
        if (SelectedDevice() is not { } d) { SetStatus(L.DevicesView_SelectADevice); return; }
        var host = string.IsNullOrEmpty(d.Hostname) ? d.DeviceId : d.Hostname;
        if (confirm)
        {
            var msg = action switch
            {
                "force-restart" => L.Format(L.DeviceCommandsPanel_ConfirmForceRestart, host),
                "logout" => L.Format(L.DeviceCommandsPanel_ConfirmLogout, host),
                _ => L.Format(L.DeviceCommandsPanel_ConfirmRestart, host),
            };
            if (MessageBox.Show(msg, L.DeviceCommandsPanel_Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        }
        try
        {
            SetStatus(L.DeviceCommandsPanel_Sending);
            var outcome = await WaitPowerAsync(await _api.PowerAsync(d.DeviceId, action));
            SetStatus(outcome switch
            {
                "scheduled" => L.DeviceCommandsPanel_Scheduled,
                "cancelled" => L.DeviceCommandsPanel_Cancelled,
                "logged-out" => L.DeviceCommandsPanel_LoggedOut,
                "no-user" => L.DeviceCommandsPanel_NoUser,
                "failed" => L.DeviceCommandsPanel_Failed,
                _ => L.DeviceCommandsPanel_NoAnswer,
            });
        }
        catch (Exception ex) { SetStatus(L.DevicesView_ConnectionError + ex.Message); }
    }

    private async Task<string> WaitPowerAsync(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce)) return "";
        for (int i = 0; i < 15; i++)
        {
            try { var o = await _api.GetAccessResultAsync(nonce); if (!string.IsNullOrEmpty(o)) return o; }
            catch { /* transient */ }
            await Task.Delay(1000);
        }
        return "";
    }

    private async Task UnlockSelectedAsync()
    {
        if (SelectedDevice() is not { } sel) { SetStatus(L.DevicesView_SelectADevice); return; }
        if (!sel.LoginLocked) { SetStatus(L.Format(L.DevicesView_IsNotSignInLocked, sel.Hostname)); return; }
        if (MessageBox.Show(L.Format(L.DevicesView_UnlockSignInOnThis, sel.Hostname), L.DevicesView_UnlockSignIn, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { await _api.UnlockDeviceAsync(sel.DeviceId); SetStatus(L.Format(L.DevicesView_SignInLockCleared, sel.Hostname)); await RefreshAsync(); }
        catch (Exception ex) { SetStatus(L.DevicesView_UnlockError + ex.Message); }
    }

    private async Task DeleteSelectedAsync()
    {
        if (SelectedDevice() is not { } sel) { SetStatus(L.DevicesView_SelectADevice); return; }
        var host = string.IsNullOrEmpty(sel.Hostname) ? sel.DeviceId : sel.Hostname;
        if (MessageBox.Show(L.Format(L.DevicesView_DeleteConfirm, host), L.DevicesView_Delete, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.DeleteDeviceAsync(sel.DeviceId); SetStatus(L.Format(L.DevicesView_Deleted, host)); await RefreshAsync(); }
        catch (Exception ex) { SetStatus(L.DevicesView_DeleteError + ex.Message); }
    }

    private async Task ApproveSelectedAsync()
    {
        if (SelectedDevice() is not { } sel) return;
        if (string.Equals(sel.Status, "Approved", StringComparison.OrdinalIgnoreCase)) { SetStatus(L.Format(L.DevicesView_IsAlreadyApproved, sel.Hostname)); return; }
        if (MessageBox.Show(L.Format(L.DevicesView_ApproveThisDevice, sel.Hostname, sel.DeviceId), L.DevicesView_Approve, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { await _api.ApproveDeviceAsync(sel.DeviceId); SetStatus(L.Format(L.DevicesView_Approved, sel.Hostname)); await RefreshAsync(); }
        catch (Exception ex) { SetStatus(L.DevicesView_ApproveError + ex.Message); }
    }

    /// <summary>Updates the viewer scale used for subsequent connections (from the operator's account preference).</summary>
    public void SetViewerScale(string scale) => _viewerScale = string.IsNullOrWhiteSpace(scale) ? "auto" : scale;

    /// <summary>Updates the viewer color depth used for subsequent connections (from the operator's account preference).</summary>
    public void SetViewerColor(string color) => _viewerColor = string.IsNullOrWhiteSpace(color) ? "full" : color;

    private void LaunchViewer(int localPort, DeviceInfo d)
    {
        var viewer = ResolveViewer();
        if (viewer is null)
        {
            SetStatus(L.DevicesView_ViewerNotFound);
            MessageBox.Show(L.DevicesView_ViewerNotFound, "VNC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // 8-bit color has no command-line flag, so write a small options file carrying it. -optionsfile takes
        // over the connection, so host/port live in the file too; the password stays on the command line
        // (plaintext, never written to disk) and -scale overrides the file's scale.
        var profile = Path.Combine(Path.GetTempPath(), $"rac-{Guid.NewGuid():N}.vnc");
        try
        {
            File.WriteAllText(profile,
                "[connection]\r\nhost=127.0.0.1\r\n" + $"port={localPort}\r\n" +
                // shared=1: request a shared session so two operators can view the same machine at once
                // (the device's TightVNC runs AlwaysShared).
                "[options]\r\n" + $"8bit={(_viewerColor == "256" ? 1 : 0)}\r\nshared=1\r\n");
        }
        catch { profile = ""; }

        var psi = new ProcessStartInfo(viewer) { UseShellExecute = false };
        if (profile.Length > 0)
            psi.ArgumentList.Add($"-optionsfile={profile}");
        else
        {
            psi.ArgumentList.Add("-host=127.0.0.1");
            psi.ArgumentList.Add($"-port={localPort}");
        }
        psi.ArgumentList.Add($"-password={d.VncSecret}");
        // Per-operator scale (TightVNC: -scale=auto -> fitWindow(true), or a percent); overrides the file.
        psi.ArgumentList.Add($"-scale={_viewerScale}");

        // Session panel layout (local per-machine preference):
        //   "split"      -> viewer in the left ~80%, info panel pinned top-right 20% (always on top)
        //   "background" -> viewer full width, info panel opens behind it (reachable from the taskbar)
        //   "off"        -> viewer full width, no info panel
        var mode = (_cfg.VncPanelMode ?? "split").Trim().ToLowerInvariant();
        var screen = Screen.FromControl(FindForm() ?? (Control)this);
        var area = screen.WorkingArea;
        int panelW = Math.Max(260, (int)(area.Width * 0.20));
        bool split = mode == "split";
        bool showPanel = split || mode == "background";
        var viewerArea = split ? new Rectangle(area.Left, area.Top, area.Width - panelW, area.Height) : area;

        // Open the panel first (note + telemetry) so the viewer opens next to / over it.
        SessionInfoWindow? info = null;
        if (showPanel)
        {
            try { info = new SessionInfoWindow(d, area, panelW, keepOnTop: split); info.Show(FindForm()); }
            catch { info = null; }
        }

        Process? p = null;
        try { p = Process.Start(psi); }
        catch (Exception ex) { SetStatus(L.DevicesView_ConnectionError + ex.Message); }

        if (p is not null)
        {
            // Best effort: size the viewer to its area; scale=auto refits the remote view.
            PositionViewer(p, viewerArea);
            // Close the side panel automatically when the viewer is closed.
            if (info is not null)
            {
                try
                {
                    p.EnableRaisingEvents = true;
                    p.Exited += (_, _) => { try { info.BeginInvoke(() => { if (!info.IsDisposed) info.Close(); }); } catch { /* ignore */ } };
                }
                catch { /* ignore */ }
            }
        }

        // The viewer reads the options file at startup; remove it shortly after (best effort; no secret in it).
        if (profile.Length > 0)
            _ = Task.Run(async () => { await Task.Delay(8000); try { File.Delete(profile); } catch { /* ignore */ } });
    }

    // Win32 to nudge the freshly launched viewer window into place beside the session panel (best effort).
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    private static void PositionViewer(Process p, Rectangle target) => _ = Task.Run(async () =>
    {
        try
        {
            IntPtr h = IntPtr.Zero;
            for (int i = 0; i < 40 && h == IntPtr.Zero; i++)
            {
                await Task.Delay(250);
                if (p.HasExited) return;
                p.Refresh();
                h = p.MainWindowHandle;
            }
            if (h == IntPtr.Zero) return;
            await Task.Delay(400);              // let the viewer finish opening before moving it
            ShowWindow(h, SW_RESTORE);          // un-maximize so the new bounds take effect
            MoveWindow(h, target.X, target.Y, target.Width, target.Height, true);
        }
        catch { /* best effort */ }
    });

    /// <summary>Locates the TightVNC viewer: configured path first, then standard install dirs and a copy next to the client.</summary>
    private string? ResolveViewer()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new[]
        {
            _cfg.ViewerExe,
            Path.Combine(pf, "TightVNC", "tvnviewer.exe"),
            string.IsNullOrEmpty(pfx86) ? "" : Path.Combine(pfx86, "TightVNC", "tvnviewer.exe"),
            Path.Combine(AppContext.BaseDirectory, "tvnviewer.exe"),
        };
        return candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
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
