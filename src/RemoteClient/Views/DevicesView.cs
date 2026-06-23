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
    private readonly Func<int, CancellationToken, Task<int>> _forward;
    private readonly ClientConfig _cfg;
    private readonly bool _isAdmin;
    private string _viewerScale;   // TightVNC viewer scale: "auto" (fit to window) or a percent "1".."400"; per-operator, from the account
    private string _viewerColor;   // TightVNC color depth: "full" or "256" (8-bit, low-color); per-operator, from the account

    private readonly List<DeviceInfo> _devices = new();
    private readonly Panel _listHost = new() { Dock = DockStyle.Fill };
    private readonly Panel _editorHost = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly ListView _list = new();
    private readonly MaterialLabel _status = new();
    private readonly TextField _search = new(L.DevicesView_SearchHostnameOrNote, 340, false, "search");
    private readonly MaterialButton _connectBtn = new() { Text = L.DevicesView_Connect, AutoSize = true };
    private readonly StatCard _statTotal = new(L.DevicesView_StatTotal);
    private readonly StatCard _statOnline = new(L.DevicesView_Online);
    private readonly StatCard _statOffline = new(L.DevicesView_Offline);
    private readonly StatCard _statPending = new(L.DevicesView_StatPending);
    private readonly UiCombo _groupFilter = new(150);
    private readonly UiCombo _statusFilter = new(140);
    private readonly ToolTip _tip = new();
    private readonly System.Windows.Forms.Timer _autoRefresh = new() { Interval = 10_000 };
    private bool _suppressFilter;
    private bool _refreshing;

    // Editor
    private readonly TabStrip _tabs = new();
    private readonly DetailHeader _header = new(L.FileManager_Files, L.DevicesView_Connect);
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
    private DeviceBetaPanel? _betaPanel;

    public DevicesView(AdminApi api, Func<int, CancellationToken, Task<int>> forward, ClientConfig cfg, bool isAdmin, string viewerScale = "auto", string viewerColor = "full")
    {
        _api = api; _forward = forward; _cfg = cfg; _isAdmin = isAdmin;
        _viewerScale = string.IsNullOrWhiteSpace(viewerScale) ? "auto" : viewerScale;
        _viewerColor = string.IsNullOrWhiteSpace(viewerColor) ? "full" : viewerColor;
        Dock = DockStyle.Fill;
        BuildList();
        BuildEditor();
        Controls.Add(_editorHost);
        Controls.Add(_listHost);
        ApplyTheme();
        // Auto-refresh the list every 10s while it is the visible view (online state + last-seen drift) — fast
        // enough to watch a device drop and come back after a restart without hitting the manual refresh.
        _autoRefresh.Tick += async (_, _) => { if (Visible && _listHost.Visible && !_refreshing) await RefreshAsync(quiet: true); };
        VisibleChanged += (_, _) => { if (!Visible) _autoRefresh.Stop(); };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _autoRefresh.Dispose();
        base.Dispose(disposing);
    }

    private void BuildList()
    {
        // Top bar: list-level actions (search + refresh).
        var tools = ViewUi.Toolbar();
        _search.Margin = new Padding(4, 0, 12, 0);
        _search.TextChanged += (_, _) => RenderList();
        tools.Controls.Add(_search);
        // Group + status filters (group is populated on each refresh).
        _statusFilter.Items.AddRange(new object[] { L.DevicesView_FilterAnyStatus, L.DevicesView_Online, L.DevicesView_LinkFlaky, L.DevicesView_Offline, L.DevicesView_StatusPending });
        _statusFilter.SelectedIndex = 0;
        _groupFilter.Margin = new Padding(0, 0, 6, 0);
        _statusFilter.Margin = new Padding(0, 0, 16, 0);
        _groupFilter.SelectedIndexChanged += (_, _) => { if (!_suppressFilter) RenderList(); };
        _statusFilter.SelectedIndexChanged += (_, _) => { if (!_suppressFilter) RenderList(); };
        tools.Controls.Add(_groupFilter);
        tools.Controls.Add(_statusFilter);
        // Refresh as a compact icon button (tooltip carries the label); device enrollment lives on Bootstrap.
        var refresh = new IconButton("refresh") { Size = new Size(38, 38), Margin = new Padding(0, 0, 0, 0) };
        _tip.SetToolTip(refresh, L.AboutView_Refresh);
        refresh.Click += async (_, _) => await RefreshAsync();
        tools.Controls.Add(refresh);

        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false;
        _list.BorderStyle = BorderStyle.None; _list.ShowItemToolTips = true;
        // Columns (redesign): Note is folded under the hostname as a subtitle, so it has no column.
        _list.Columns.Add(L.DevicesView_Device, 300);
        _list.Columns.Add(L.BootstrapView_Group, 130);
        _list.Columns.Add(L.BootstrapView_Status, 120);
        _list.Columns.Add(L.CredentialDialog_User, 160);
        _list.Columns.Add(L.DevicesView_LastOnline, 150);
        _list.Columns.Add(L.DeviceTelemetryPanel_PublicIP, 170);
        // Owner-drawn rows: status pills, mono cells, hostname + note subtitle, hover. Taller rows via image-list.
        _list.OwnerDraw = true;
        _list.SmallImageList = new ImageList { ImageSize = new Size(1, 46) };
        TryEnableDoubleBuffer(_list);
        _list.DrawColumnHeader += DrawHeader;
        _list.DrawItem += DrawRow;
        _list.DrawSubItem += DrawCell;
        _list.SizeChanged += (_, _) => LayoutColumns();   // re-fill the Public IP column on resize
        _list.MouseMove += OnRowHover;
        _list.MouseLeave += (_, _) => SetHoverRow(-1);
        _list.DoubleClick += async (_, _) => await ConnectSelectedAsync();
        // Right-click selects the row under the cursor and opens a context menu of editor tabs.
        if (_isAdmin)
        {
            var menu = UiMenu.Themed();
            void Item(string text, string tab) => menu.Items.Add(text, null, async (_, _) => await EditSelectedAsync(tab));
            Item(L.DevicesView_Properties, "general");
            Item(L.DevicesView_Messages, "messages");
            Item("Log", "log");
            Item(L.DevicesView_Telemetry, "telemetry");
            Item(L.UsersView_Permissions, "permissions");

            // File manager (two-pane), separated like the power commands below.
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L.FileManager_Files, null, async (_, _) => await OpenFilesSelectedAsync());

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

        _listHost.Controls.Add(ViewUi.Rows(1, tools, _list, actions, ViewUi.StatusHost(_status)));  // Fill

        // Stat row (Total / Online / Offline / Pending), docked above the list.
        _statOnline.ValueColor = ThemeManager.OkFg;
        _statOffline.ValueColor = ThemeManager.Text2;
        _statPending.ValueColor = ThemeManager.WarnFg;
        var statGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0) };
        for (int i = 0; i < 4; i++) statGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        var cards = new[] { _statTotal, _statOnline, _statOffline, _statPending };
        for (int i = 0; i < cards.Length; i++)
        {
            cards[i].Dock = DockStyle.Fill;
            cards[i].Margin = new Padding(i == 0 ? 0 : 6, 0, 0, 0);
            statGrid.Controls.Add(cards[i], i, 0);
        }
        var statRow = new Panel { Dock = DockStyle.Top, Height = 72, Padding = new Padding(10, 8, 10, 4) };
        statRow.Controls.Add(statGrid);
        _listHost.Controls.Add(statRow);  // Top
    }

    private void BuildEditor()
    {
        _header.Back.Click += async (_, _) => { ShowList(); await RefreshAsync(); };
        _header.Connect.Click += async (_, _) => { if (_editing is { } d) await ConnectDeviceAsync(d); };
        _header.Files.Click += async (_, _) => await OpenFilesSelectedAsync();
        _header.More.Click += (_, _) => ShowMoreMenu();
        _tabs.TabSelected += SelectTab;

        _tabContent.Dock = DockStyle.Fill;
        _editorHost.Controls.Add(_tabContent);   // Fill
        _editorHost.Controls.Add(_tabs);         // Top (tab strip)
        _editorHost.Controls.Add(_header);       // Top (added last -> topmost)
    }

    private void ShowMoreMenu()
    {
        if (_editing is null) return;
        var menu = UiMenu.Themed();
        menu.Items.Add(L.FileManager_Files, null, async (_, _) => await OpenFilesSelectedAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.DeviceCommandsPanel_Restart, null, async (_, _) => await RunPowerAsync("restart", confirm: true));
        menu.Items.Add(L.DeviceCommandsPanel_ForceRestart, null, async (_, _) => await RunPowerAsync("force-restart", confirm: true));
        menu.Items.Add(L.DeviceCommandsPanel_CancelRestart, null, async (_, _) => await RunPowerAsync("cancel", confirm: false));
        menu.Items.Add(L.DeviceCommandsPanel_Logout, null, async (_, _) => await RunPowerAsync("logout", confirm: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.DevicesView_Delete, null, async (_, _) => await DeleteSelectedAsync());
        menu.Show(_header.More, new Point(0, _header.More.Height));
    }

    public void ApplyTheme()
    {
        ThemeManager.StyleView(this, _list);
        _statTotal.ValueColor = ThemeManager.Text;
        _statOffline.ValueColor = ThemeManager.Text2;
        foreach (var c in new[] { _statTotal, _statOnline, _statOffline, _statPending }) c.Invalidate();
    }

    public async Task OnShownAsync() { ShowList(); _autoRefresh.Start(); await RefreshAsync(); }

    /// <summary>Topbar subtitle: live enrolled / online counts.</summary>
    public string? Subtitle => L.Format(L.DevicesView_TopbarSubtitle, _devices.Count, _devices.Count(d => d.Online));

    private void ShowList() { _editorHost.Visible = false; _listHost.Visible = true; _listHost.BringToFront(); }
    private void ShowEditor() { _listHost.Visible = false; _editorHost.Visible = true; _editorHost.BringToFront(); }

    private DeviceInfo? SelectedDevice() => _list.SelectedItems.Count == 0 ? null : (DeviceInfo)_list.SelectedItems[0].Tag!;

    private async Task RefreshAsync(bool quiet = false)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            if (!quiet) SetStatus(L.DevicesView_FetchingDeviceList);   // quiet = the silent 10-second auto-refresh
            var devices = await RetryAsync(() => _api.GetDevicesAsync());
            _devices.Clear(); _devices.AddRange(devices);
            PopulateGroupFilter();
            RenderList();
            UpdateStats();
            SetStatus(L.Format(L.DevicesView_Device_2, devices.Count));
        }
        catch (Exception ex) { SetStatus(L.DevicesView_ListError + ex.Message); }
        finally { _refreshing = false; }
    }

    // ---- Owner-drawn table (design_handoff_console_redesign) --------------------------------
    private int _hoverRow = -1;

    private static void TryEnableDoubleBuffer(ListView list)
    {
        try
        {
            typeof(ListView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(list, true);
        }
        catch { /* purely cosmetic */ }
    }

    private void SetHoverRow(int idx)
    {
        if (_hoverRow == idx) return;
        _hoverRow = idx;
        _list.Invalidate();
    }

    private void OnRowHover(object? sender, MouseEventArgs e) => SetHoverRow(_list.GetItemAt(e.X, e.Y)?.Index ?? -1);

    private void DrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        var g = e.Graphics;
        using (var b = new SolidBrush(ThemeManager.Panel2)) g.FillRectangle(b, e.Bounds);
        using (var pen = new Pen(ThemeManager.BorderSoft)) g.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        TextRenderer.DrawText(g, (e.Header?.Text ?? "").ToUpperInvariant(), UiFont.Label,
            new Rectangle(e.Bounds.Left + 10, e.Bounds.Top, e.Bounds.Width - 14, e.Bounds.Height), ThemeManager.Text3,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    private void DrawRow(object? sender, DrawListViewItemEventArgs e)
    {
        var g = e.Graphics;
        Color bg = e.Item.Selected ? ThemeManager.AccentSoft : e.ItemIndex == _hoverRow ? ThemeManager.Panel2 : ThemeManager.Panel;
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, e.Bounds);
        // Paint every cell here: DrawItem fires reliably for Details, whereas DrawSubItem can silently
        // not fire (which left the cells blank). Cell rects are derived from the column widths so this
        // tracks horizontal scrolling via e.Bounds.Left.
        if (e.Item.Tag is DeviceInfo d)
        {
            int x = e.Bounds.Left;
            for (int i = 0; i < _list.Columns.Count; i++)
            {
                int w = _list.Columns[i].Width;
                var cell = new Rectangle(x, e.Bounds.Top, w, e.Bounds.Height);
                g.SetClip(cell);
                PaintCell(g, i, d, cell);
                g.ResetClip();
                x += w;
            }
        }
        using (var pen = new Pen(ThemeManager.BorderSoft)) g.DrawLine(pen, e.Bounds.Left + 8, e.Bounds.Bottom - 1, e.Bounds.Right - 8, e.Bounds.Bottom - 1);
    }

    // Subitems are painted by DrawRow; suppress the default text (kept on the item only for auto-size).
    private void DrawCell(object? sender, DrawListViewSubItemEventArgs e) => e.DrawDefault = false;

    private void PaintCell(Graphics g, int col, DeviceInfo d, Rectangle r)
    {
        int cy = r.Top + r.Height / 2;
        switch (col)
        {
            case 0: // Device: icon tile + (amber lock) + hostname + note subtitle
            {
                var tile = new Rectangle(r.Left + 10, cy - 15, 30, 30);
                UiPaint.FillRoundedRect(g, tile, 8, ThemeManager.Panel3);
                UiIcons.Draw(g, "monitor", new RectangleF(tile.X + 6, tile.Y + 6, 18, 18), ThemeManager.Text2);
                string name = string.IsNullOrEmpty(d.Hostname) ? L.DevicesView_Unnamed : d.Hostname;
                string? note = string.IsNullOrWhiteSpace(d.Note) ? null : d.Note;
                int tx = tile.Right + 11;
                int nameY = note is null ? r.Top : cy - 17;
                int nameH = note is null ? r.Height : 17;
                var nameFlags = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis
                                | (note is null ? TextFormatFlags.VerticalCenter : TextFormatFlags.Default);
                if (d.LoginLocked)
                {
                    UiIcons.Draw(g, "lock", new RectangleF(tx, (note is null ? cy : cy - 9) - 7, 14, 14), ThemeManager.WarnFg, 1.5f);
                    tx += 19;
                }
                TextRenderer.DrawText(g, name, UiFont.MonoSemi, new Rectangle(tx, nameY, r.Right - tx - 8, nameH), ThemeManager.Text, nameFlags);
                if (note is not null)
                    TextRenderer.DrawText(g, note, UiFont.Small, new Rectangle(tile.Right + 11, cy + 1, r.Right - tile.Right - 19, 15),
                        ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                break;
            }
            case 2: // Online status pill (online / flaky / offline / pending)
            {
                var (txt, fg, bgc) = StatusPill(d);
                UiPaint.DrawPill(g, r.Left + 10, cy, txt, fg, bgc, UiFont.Small, dot: true);
                break;
            }
            default:
            {
                (string text, Color color, Font font) = col switch
                {
                    1 => (d.GroupName ?? "—", ThemeManager.Text2, UiFont.Body),
                    3 => (string.IsNullOrWhiteSpace(d.LoggedInUser) ? "—" : d.LoggedInUser!, ThemeManager.Text2, UiFont.Body),
                    4 => (d.Online ? L.DevicesView_JustNow : RelativeTime(d.LastSeenAt), ThemeManager.Text2, UiFont.Body),
                    5 => (DeviceTelemetryPanel.PublicIp(d),
                          d.PublicIpReverse is { } rev && rev.Contains("nat.", StringComparison.OrdinalIgnoreCase) ? ThemeManager.DangerFg : ThemeManager.Text2,
                          UiFont.Mono),
                    _ => ("", ThemeManager.Text2, UiFont.Body),
                };
                TextRenderer.DrawText(g, text, font, new Rectangle(r.Left + 10, r.Top, r.Width - 14, r.Height), color,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                break;
            }
        }
    }

    /// <summary>Status pill (text + fg/soft-bg) shared by the list cell and the detail header.</summary>
    private static (string Text, Color Fg, Color Bg) StatusPill(DeviceInfo d)
    {
        if (string.Equals(d.Status, "Pending", StringComparison.OrdinalIgnoreCase)) return (L.DevicesView_StatusPending, ThemeManager.WarnFg, ThemeManager.WarnBg);
        if (d.Online) return (L.DevicesView_Online, ThemeManager.OkFg, ThemeManager.OkBg);
        if (d.LinkFlaky) return (L.DevicesView_LinkFlaky, ThemeManager.WarnFg, ThemeManager.WarnBg);
        return (L.DevicesView_Offline, ThemeManager.OffFg, ThemeManager.OffBg);
    }

    /// <summary>Human "last online" like the design: just now / N min ago / Nh ago / Nd ago / date.</summary>
    private static string RelativeTime(DateTimeOffset? when)
    {
        if (when is not { } t) return "—";
        var ago = DateTimeOffset.UtcNow - t.ToUniversalTime();
        if (ago < TimeSpan.Zero) ago = TimeSpan.Zero;
        if (ago.TotalSeconds < 45) return L.DevicesView_JustNow;
        if (ago.TotalMinutes < 60) return L.Format(L.DevicesView_MinutesAgo, (int)ago.TotalMinutes);
        if (ago.TotalHours < 24) return L.Format(L.DevicesView_HoursAgo, (int)ago.TotalHours);
        if (ago.TotalDays < 30) return L.Format(L.DevicesView_DaysAgo, (int)ago.TotalDays);
        return t.LocalDateTime.ToString("yyyy-MM-dd");
    }

    private void PopulateGroupFilter()
    {
        var sel = _groupFilter.SelectedItem as string;
        _suppressFilter = true;
        _groupFilter.Items.Clear();
        _groupFilter.Items.Add(L.DevicesView_FilterAllGroups);
        foreach (var name in _devices.Select(d => d.GroupName).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().OrderBy(g => g))
            _groupFilter.Items.Add(name!);
        int idx = sel is null ? 0 : _groupFilter.Items.IndexOf(sel);
        _groupFilter.SelectedIndex = idx < 0 ? 0 : idx;
        _suppressFilter = false;
    }

    private void UpdateStats()
    {
        _statTotal.SetValue(_devices.Count);
        _statOnline.SetValue(_devices.Count(d => d.Online));
        _statOffline.SetValue(_devices.Count(d => !d.Online));
        _statPending.SetValue(_devices.Count(d => string.Equals(d.Status, "Pending", StringComparison.OrdinalIgnoreCase)));
    }

    private void RenderList()
    {
        var q = _search.Text.Trim();
        IEnumerable<DeviceInfo> items = _devices;
        if (q.Length > 0)
            items = items.Where(d =>
                (d.Hostname?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.GroupName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.Note?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));

        if (_groupFilter.SelectedIndex > 0 && _groupFilter.SelectedItem is string gn)
            items = items.Where(d => string.Equals(d.GroupName, gn, StringComparison.OrdinalIgnoreCase));

        items = _statusFilter.SelectedIndex switch
        {
            1 => items.Where(d => d.Online),
            2 => items.Where(d => d.LinkFlaky && !d.Online),
            3 => items.Where(d => !d.Online && !d.LinkFlaky),
            4 => items.Where(d => string.Equals(d.Status, "Pending", StringComparison.OrdinalIgnoreCase)),
            _ => items,
        };

        items = SortItems(items);

        // Preserve the operator's selection + scroll across a rebuild (especially the 10-second auto-refresh).
        var selId = SelectedDevice()?.DeviceId;
        var topId = (_list.TopItem?.Tag as DeviceInfo)?.DeviceId;

        _list.BeginUpdate();
        _list.Items.Clear();
        ListViewItem? toSelect = null, toTop = null;
        foreach (var d in items)
        {
            // Display is owner-drawn (DrawRow). The sub-item text is kept (not shown) only so the built-in
            // column auto-size has real content to measure instead of collapsing to zero.
            string status = string.Equals(d.Status, "Pending", StringComparison.OrdinalIgnoreCase) ? "pending"
                          : d.Online ? "online" : d.LinkFlaky ? "flaky" : "offline";
            var item = new ListViewItem(string.IsNullOrEmpty(d.Hostname) ? L.DevicesView_Unnamed : d.Hostname) { Tag = d };
            item.SubItems.Add(d.GroupName ?? "—");
            item.SubItems.Add(status);
            item.SubItems.Add(string.IsNullOrWhiteSpace(d.LoggedInUser) ? "—" : d.LoggedInUser);
            item.SubItems.Add(RelativeTime(d.LastSeenAt));
            item.SubItems.Add(DeviceTelemetryPanel.PublicIp(d));
            if (d.LoginLocked) item.ToolTipText = L.Format(L.DevicesView_SignInLockedFailedAttempts, d.LoginFailCount);
            else if (d.LinkFlaky) item.ToolTipText = L.Format(L.DevicesView_LinkFlakyTip, d.RecentReconnects);
            else if (!string.IsNullOrWhiteSpace(d.LastIncident)) item.ToolTipText = "Supervisor: " + d.LastIncident;
            _list.Items.Add(item);
            if (d.DeviceId == selId) toSelect = item;
            if (d.DeviceId == topId) toTop = item;
        }
        if (toSelect is not null) { toSelect.Selected = true; toSelect.Focused = true; }
        _list.EndUpdate();
        try { if (toTop is not null) _list.TopItem = toTop; else toSelect?.EnsureVisible(); } catch { /* TopItem can throw mid-layout */ }
        LayoutColumns();
    }

    // Gép stays a comfortable fixed width; Állapot + Utoljára online are compact; Csoport + Felhasználó size to
    // their content; Publikus IP takes whatever width is left so the row fills without clipping the others.
    private void LayoutColumns()
    {
        if (_list.Columns.Count < 6 || _list.ClientSize.Width <= 4) return;
        int Fit(int col, Func<DeviceInfo, string?> sel, Font f, int min, int max)
        {
            int n = TextRenderer.MeasureText(_list.Columns[col].Text.ToUpperInvariant(), UiFont.Label).Width;
            foreach (var d in _devices) n = Math.Max(n, TextRenderer.MeasureText(sel(d) ?? "—", f).Width);
            return Math.Clamp(n + 24, min, max);
        }
        _list.BeginUpdate();
        _list.Columns[0].Width = 300;                                                   // Gép (fixed)
        _list.Columns[2].Width = 96;                                                    // Állapot (status pill)
        _list.Columns[4].Width = 96;                                                    // Utoljára online
        _list.Columns[1].Width = Fit(1, d => d.GroupName, UiFont.Body, 92, 180);        // Csoport (content)
        _list.Columns[3].Width = Fit(3, d => d.LoggedInUser, UiFont.Body, 120, 240);    // Felhasználó (content)
        int used = 300 + 96 + 96 + _list.Columns[1].Width + _list.Columns[3].Width;
        _list.Columns[5].Width = Math.Max(170, _list.ClientSize.Width - used - 2);       // Publikus IP fills the rest
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
            2 => d => d.Online,
            3 => d => d.LoggedInUser,
            4 => d => d.LastSeenAt,
            5 => d => d.PublicIpAddress,
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
            var (st, sf, sb) = StatusPill(d);
            string sub = string.Join(" · ", new[] { d.Note, d.GroupName, d.OsVersion }.Where(s => !string.IsNullOrWhiteSpace(s)));
            _header.SetDevice(string.IsNullOrEmpty(d.Hostname) ? d.DeviceId : d.Hostname, sub, st, sf, sb);

            _generalPanel?.Dispose(); _permPanel?.Dispose(); _msgPanel?.Dispose(); _cmdPanel?.Dispose(); _logPanel?.Dispose(); _telemetryPanel?.Dispose(); _betaPanel?.Dispose();
            _generalPanel = new DeviceGeneralPanel(_api, d, groups);
            _permPanel = new DevicePermissionsPanel(_api, d);
            _msgPanel = new DeviceMessagesPanel(_api, d, () => ConnectDeviceAsync(d));
            _cmdPanel = new DeviceCommandsPanel(_api, d);
            _logPanel = new LogPanel(_api, deviceId: d.DeviceId);
            _telemetryPanel = new DeviceTelemetryPanel(d);
            _betaPanel = new DeviceBetaPanel(_api, d);

            // BETA tab only for beta-channel devices: the agent that understands the transport ships there first.
            var tabs = new List<(string, string)>
            {
                ("general", L.ChannelsView_General), ("permissions", L.UsersView_Permissions),
                ("messages", L.DevicesView_Messages), ("commands", L.DevicesView_Commands),
                ("log", L.MainForm_Log), ("telemetry", L.DevicesView_Telemetry),
            };
            if (string.Equals(d.Channel, "beta", StringComparison.OrdinalIgnoreCase)) tabs.Add(("beta", "BETA"));
            _tabs.SetTabs(tabs.ToArray(), initialTab);

            ShowEditor();
            SelectTab(initialTab);
        }
        catch (Exception ex) { SetStatus(L.DevicesView_PropertiesError + ex.Message); }
    }

    private void SelectTab(string tab) => _ = SelectTabAsync(tab);

    private async Task SelectTabAsync(string tab)
    {
        _tabs.SetActive(tab);
        _tabContent.Controls.Clear();
        switch (tab)
        {
            case "general" when _generalPanel is not null: _tabContent.Controls.Add(_generalPanel); break;
            case "permissions" when _permPanel is not null: _tabContent.Controls.Add(_permPanel); break;
            case "messages" when _msgPanel is not null: _tabContent.Controls.Add(_msgPanel); break;
            case "commands" when _cmdPanel is not null: _tabContent.Controls.Add(_cmdPanel); break;
            case "telemetry" when _telemetryPanel is not null: _tabContent.Controls.Add(_telemetryPanel); break;
            case "log" when _logPanel is not null: _tabContent.Controls.Add(_logPanel); await _logPanel.ShownAsync(); break;
            case "beta" when _betaPanel is not null: _tabContent.Controls.Add(_betaPanel); break;
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
            var result = await _api.OpenTunnelAsync(d.DeviceId, "vnc");
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
            var localPort = await _forward(result.RemotePort, CancellationToken.None);
            ShowSleepWarning(d);
            LaunchViewer(localPort, d);
            SetStatus(L.Format(L.DevicesView_VNCStarted, d.Hostname));
        }
        catch (Exception ex) { SetStatus(L.DevicesView_ConnectionError + ex.Message); }
        finally { _connectBtn.Enabled = true; }
    }

    private async Task OpenFilesSelectedAsync()
    {
        if (SelectedDevice() is not { } d) { SetStatus(L.DevicesView_SelectADevice); return; }
        await OpenFilesAsync(d);
    }

    /// <summary>Opens the tunnel and launches the two-pane file manager (uses the session file port + token).</summary>
    private async Task OpenFilesAsync(DeviceInfo sel)
    {
        try
        {
            SetStatus(L.DevicesView_FetchingLatestData);
            var devices = await _api.GetDevicesAsync();
            var d = devices.FirstOrDefault(x => x.DeviceId == sel.DeviceId) ?? sel;
            if (!d.Online) { MessageBox.Show(L.DevicesView_TheDeviceIsOffline, "Offline", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            SetStatus(L.Format(L.DevicesView_OpeningTunnel, d.Hostname));
            var result = await _api.OpenTunnelAsync(d.DeviceId, "file");
            if (result is null || result.FileRemotePort <= 0 || string.IsNullOrEmpty(result.FileToken)) { SetStatus(L.DevicesView_TunnelRequestFailed); return; }

            SetStatus(L.DevicesView_WaitingForTheRemoteDevice);
            var outcome = await WaitAccessAsync(result.Nonce);
            if (outcome is not ("auto" or "granted")) { SetStatus(L.DevicesView_Denied); return; }

            SetStatus(L.DevicesView_ReachingBastionPortThroughThe);
            await Task.Delay(1500);
            var localPort = await _forward(result.FileRemotePort, CancellationToken.None);
            new FileManagerWindow(localPort, result.FileToken!, string.IsNullOrEmpty(d.Hostname) ? d.DeviceId : d.Hostname).Show();
            SetStatus(L.FileManager_Title);
        }
        catch (Exception ex) { SetStatus(L.DevicesView_ConnectionError + ex.Message); }
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

    /// <summary>On connect, warns when the machine is likely to sleep mid-session (drops the VNC link): on
    /// battery, or on AC with a non-"never" standby timeout. No-op until an updated agent reports power.</summary>
    private void ShowSleepWarning(DeviceInfo d)
    {
        if (d.BatteryPercent is null && d.SleepAcMinutes is null && d.SleepDcMinutes is null) return; // no power telemetry yet
        string Sleep(int? m) => m switch { null => "?", 0 => L.DeviceTelemetryPanel_Never, _ => L.Format(L.DeviceTelemetryPanel_Minutes, m.Value) };
        string? msg = !d.AcOnline
            ? L.Format(L.DevicesView_SleepWarnBattery, d.BatteryPercent?.ToString() ?? "?", Sleep(d.SleepDcMinutes))
            : d.SleepAcMinutes is int ac && ac > 0 ? L.Format(L.DevicesView_SleepWarnCharger, ac) : null;
        if (msg is not null)
            MessageBox.Show(this, msg, L.DevicesView_SleepWarnTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

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
            try { info = new SessionInfoWindow(_api, d, area, panelW, keepOnTop: split); info.Show(FindForm()); }
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
