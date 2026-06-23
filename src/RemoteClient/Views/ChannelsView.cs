using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// Release channels: RTM | BETA component cards (owner-drawn tables + Rollout/Promote) over a wide
/// device component-versions table, with Upload EXE / Build MSI editors. See design_handoff_console_redesign.
/// </summary>
public sealed class ChannelsView : UserControl, IContentView
{
    private readonly AdminApi _api;
    private readonly Panel _mainHost = new() { Dock = DockStyle.Fill, Padding = new Padding(22, 14, 22, 12) };
    private readonly Panel _editorHost = new() { Dock = DockStyle.Fill, Padding = new Padding(22, 12, 22, 18), Visible = false };
    private readonly OwnerList _rtmList = new(40);
    private readonly OwnerList _betaList = new(40);
    private readonly OwnerList _deviceVerList = new(44);
    private readonly List<DeviceInfo> _devices = new();
    private readonly List<ChannelPackageInfo> _rtmPkgs = new();
    private readonly List<ChannelPackageInfo> _betaPkgs = new();
    private readonly System.Windows.Forms.Timer _devTimer = new() { Interval = 30000 };
    private bool _devRefreshing;
    private readonly MaterialLabel _status = new() { Dock = DockStyle.Fill };

    private readonly IconButton _back = new("chevron");
    private string _editorTitle = "";
    private readonly Panel _editorContent = new() { Dock = DockStyle.Fill };
    private Control? _editorPanel;

    public ChannelsView(AdminApi api)
    {
        _api = api;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;
        BuildMain();
        BuildEditor();
        Controls.Add(_editorHost);
        Controls.Add(_mainHost);

        _devTimer.Tick += async (_, _) => { if (_mainHost.Visible) await RefreshDevicesAsync(); };
        _devTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _devTimer.Dispose();
        base.Dispose(disposing);
    }

    private void BuildMain()
    {
        _rtmList.SetColumns(ChanCols());
        _betaList.SetColumns(ChanCols());
        _rtmList.PaintRow += PaintPkgRow;
        _betaList.PaintRow += PaintPkgRow;

        _deviceVerList.SetColumns(
            new OwnerList.Col(L.DevicesView_Device, 220), new OwnerList.Col(L.DeviceTelemetryPanel_Channel, 90),
            new OwnerList.Col(L.DevicesView_Update, 150), new OwnerList.Col("Agent", 110),
            new OwnerList.Col("Client", 110), new OwnerList.Col("Updater", 110), new OwnerList.Col("VNC", 92));
        _deviceVerList.PaintRow += PaintDevRow;

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 252, ColumnCount = 2, RowCount = 1, BackColor = ThemeManager.Bg };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.Controls.Add(ChannelColumn("RTM", _rtmList, false, () => _ = RolloutChannelAsync("rtm", _rtmList, _rtmPkgs), null), 0, 0);
        grid.Controls.Add(ChannelColumn("BETA", _betaList, true, () => _ = RolloutChannelAsync("beta", _betaList, _betaPkgs), () => _ = PromoteChannelAsync()), 1, 0);

        var dev = new Panel { Dock = DockStyle.Fill, BackColor = ThemeManager.Bg };
        var devLabel = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = ThemeManager.Bg };
        devLabel.Paint += (_, e) => TextRenderer.DrawText(e.Graphics, L.ChannelsView_DeviceVersions, UiFont.SectionTitle, new Rectangle(2, 4, 400, 20), ThemeManager.Text, TextFormatFlags.Left | TextFormatFlags.NoPadding);
        _deviceVerList.Dock = DockStyle.Fill;
        dev.Controls.Add(_deviceVerList);
        dev.Controls.Add(devLabel);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 50, BackColor = ThemeManager.Bg };
        var upload = new UiButton(L.ChannelsView_UploadExe, UiButton.Style.Outline) { Location = new Point(0, 8) };
        upload.Click += (_, _) => OpenUpload();
        var msi = new UiButton(L.ChannelsView_BuildMSI, UiButton.Style.Outline) { Location = new Point(upload.Width + 10, 8) };
        msi.Click += async (_, _) => await OpenMsiAsync();
        bottom.Controls.Add(upload);
        bottom.Controls.Add(msi);

        var statusHost = new Panel { Dock = DockStyle.Bottom, Height = 22, BackColor = ThemeManager.Bg };
        statusHost.Controls.Add(_status);

        _mainHost.Controls.Add(dev);
        _mainHost.Controls.Add(bottom);
        _mainHost.Controls.Add(statusHost);
        _mainHost.Controls.Add(grid);
    }

    private static OwnerList.Col[] ChanCols() =>
    [
        new OwnerList.Col(L.ChannelsView_Component, 150), new OwnerList.Col(L.ChannelsView_Version, 92),
        new OwnerList.Col(L.ChannelsView_Uploaded, 140), new OwnerList.Col(L.ChannelsView_Released, 70),
    ];

    private Panel ChannelColumn(string name, OwnerList list, bool isBeta, Action rollout, Action? promote)
    {
        var col = new Panel { Dock = DockStyle.Fill, BackColor = ThemeManager.Bg, Margin = new Padding(0, 0, isBeta ? 0 : 8, 0) };
        var header = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = ThemeManager.Bg };
        header.Paint += (_, e) =>
        {
            var (fg, bg) = isBeta ? (ThemeManager.BetaFg, ThemeManager.BetaBg) : (ThemeManager.Text2, ThemeManager.Panel3);
            UiPaint.DrawPill(e.Graphics, 2, 22, name, fg, bg, UiFont.Label, false);
        };
        var roll = new UiButton($"Rollout {name}", UiButton.Style.Filled);
        roll.Click += (_, _) => rollout();
        header.Controls.Add(roll);
        UiButton? prom = null;
        if (promote is not null)
        {
            prom = new UiButton("Promote → RTM", UiButton.Style.Outline);
            prom.Click += (_, _) => promote();
            header.Controls.Add(prom);
        }
        header.Resize += (_, _) =>
        {
            roll.Location = new Point(header.Width - roll.Width, 4);
            if (prom is not null) prom.Location = new Point(roll.Left - 8 - prom.Width, 4);
        };
        list.Dock = DockStyle.Fill;
        col.Controls.Add(list);
        col.Controls.Add(header);
        return col;
    }

    private void PaintPkgRow(object? sender, RowPaintEventArgs e)
    {
        var p = (ChannelPackageInfo)e.Item;
        e.Text(0, Cap(p.Component), UiFont.Body, ThemeManager.Text);
        e.Text(1, p.Version, UiFont.Mono, ThemeManager.Text2);
        e.Text(2, p.UploadedAt.LocalDateTime.ToString("yyyy.MM.dd HH:mm"), UiFont.MonoSmall, ThemeManager.Text3);
        bool rel = RolledOut(p.Channel, p.Component, p.Version, _devices);
        var c3 = e.Cell(3);
        TextRenderer.DrawText(e.G, rel ? "✓" : "—", UiFont.MonoSemi, new Rectangle(c3.Left, c3.Top, 30, c3.Height),
            rel ? ThemeManager.OkFg : ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void PaintDevRow(object? sender, RowPaintEventArgs e)
    {
        var d = (DeviceInfo)e.Item;
        e.Text(0, string.IsNullOrEmpty(d.Hostname) ? d.DeviceId : d.Hostname, UiFont.MonoSemi, ThemeManager.Text);
        bool beta = string.Equals(d.Channel, "beta", StringComparison.OrdinalIgnoreCase);
        var (cfg, cbg) = beta ? (ThemeManager.BetaFg, ThemeManager.BetaBg) : (ThemeManager.Text2, ThemeManager.Panel3);
        UiPaint.DrawPill(e.G, e.Cell(1).Left, e.Cy, beta ? "BETA" : "rtm", cfg, cbg, UiFont.Label, false);
        e.Text(2, d.UpdatePending ? "✓ " + (d.UpdatePendingInfo ?? "") : "—", UiFont.Mono, d.UpdatePending ? ThemeManager.WarnFg : ThemeManager.Text3);
        e.Text(3, S(d.AgentVersion), UiFont.Mono, ThemeManager.Text2);
        e.Text(4, S(d.ClientVersion), UiFont.Mono, ThemeManager.Text2);
        e.Text(5, S(d.HelperVersion), UiFont.Mono, ThemeManager.Text2);
        e.Text(6, S(d.VncVersion), UiFont.Mono, ThemeManager.Text2);
    }

    private void BuildEditor()
    {
        _back.SetBounds(0, 8, 36, 36);
        _back.Click += async (_, _) => { ShowMain(); await RefreshAsync(); };
        var header = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = ThemeManager.Bg };
        header.Controls.Add(_back);
        header.Paint += (_, e) => TextRenderer.DrawText(e.Graphics, _editorTitle, UiFont.PageTitle,
            new Rectangle(48, 0, header.Width - 48, 46), ThemeManager.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        _editorHost.Controls.Add(_editorContent);
        _editorHost.Controls.Add(header);
    }

    public void ApplyTheme() { BackColor = _mainHost.BackColor = _editorHost.BackColor = ThemeManager.Bg; Invalidate(true); }

    public async Task OnShownAsync() { ShowMain(); await RefreshAsync(); }

    private void ShowMain() { _editorHost.Visible = false; _mainHost.Visible = true; _mainHost.BringToFront(); }
    private void ShowEditor() { _mainHost.Visible = false; _editorHost.Visible = true; _editorHost.BringToFront(); _editorHost.Invalidate(true); }

    private async Task RefreshAsync()
    {
        try
        {
            var ch = await _api.GetChannelsAsync();
            var devices = await _api.GetDevicesAsync();
            _devices.Clear(); _devices.AddRange(devices);
            FillChannel(_rtmList, _rtmPkgs, ch.Where(p => string.Equals(p.Channel, "rtm", StringComparison.OrdinalIgnoreCase)));
            FillChannel(_betaList, _betaPkgs, ch.Where(p => string.Equals(p.Channel, "beta", StringComparison.OrdinalIgnoreCase)));
            FillDevices();
            _status.Text = ch.Count == 0 ? L.ChannelsView_NoPackagesHaveBeenUploaded : L.Format(L.ChannelsView_RTMBETAComponents, _rtmPkgs.Count, _betaPkgs.Count);
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private async Task RefreshDevicesAsync()
    {
        if (_devRefreshing) return;
        _devRefreshing = true;
        try
        {
            var devices = await _api.GetDevicesAsync();
            _devices.Clear(); _devices.AddRange(devices);
            FillDevices();
        }
        catch { /* transient; refreshed on the next tick */ }
        finally { _devRefreshing = false; }
    }

    private static void FillChannel(OwnerList list, List<ChannelPackageInfo> store, IEnumerable<ChannelPackageInfo> items)
    {
        store.Clear();
        store.AddRange(items.OrderBy(p => p.Component));
        list.BeginUpdate();
        list.Clear();
        foreach (var p in store) list.Add(p);
        list.EndUpdate();
    }

    private void FillDevices()
    {
        _deviceVerList.BeginUpdate();
        _deviceVerList.Clear();
        foreach (var d in _devices.OrderBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase)) _deviceVerList.Add(d);
        _deviceVerList.EndUpdate();
    }

    private static string S(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v;
    private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    private static bool RolledOut(string channel, string comp, string version, List<DeviceInfo> devices)
    {
        var relevant = devices.Where(d =>
            string.Equals(d.Channel, channel, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.Status, "Approved", StringComparison.OrdinalIgnoreCase) &&
            d.UpdateAllowed).ToList();
        if (relevant.Count == 0) return false;
        return relevant.All(d => Reported(d, comp).StartsWith(version, StringComparison.OrdinalIgnoreCase));
    }

    private static string Reported(DeviceInfo d, string comp) => (comp switch
    {
        "updater" => d.HelperVersion,
        "vnc" => d.VncVersion,
        "client" => d.ClientVersion,
        _ => d.AgentVersion,
    }) ?? "";

    private static string? SelectedComp(OwnerList list) => (list.Selected as ChannelPackageInfo)?.Component;

    private async Task RolloutChannelAsync(string channel, OwnerList list, List<ChannelPackageInfo> pkgs)
    {
        var sel = SelectedComp(list);
        var comps = sel is not null ? new[] { sel } : pkgs.Select(p => p.Component).ToArray();
        if (comps.Length == 0) { _status.Text = L.ChannelsView_NoPackageOnThisChannel; return; }
        var what = sel is not null ? L.Format(L.ChannelsView_X, sel) : L.Format(L.ChannelsView_ALL, string.Join(", ", comps));
        if (MessageBox.Show(L.Format(L.ChannelsView_ReleaseTheChannelSComponent, channel.ToUpperInvariant(), what), "Rollout", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        var done = new List<string>();
        foreach (var c in comps)
        {
            try { await _api.RolloutAsync(channel, c); done.Add($"{c} ✓"); }
            catch (Exception ex) { done.Add($"{c} ✗ ({ex.Message})"); }
        }
        _status.Text = "Rollout — " + string.Join(" · ", done);
    }

    private async Task PromoteChannelAsync()
    {
        var sel = SelectedComp(_betaList);
        var comps = sel is not null ? new[] { sel } : _betaPkgs.Select(p => p.Component).ToArray();
        if (comps.Length == 0) { _status.Text = L.ChannelsView_NoPackageOnTheBETA; return; }
        var what = sel is not null ? L.Format(L.ChannelsView_X, sel) : L.Format(L.ChannelsView_ALL, string.Join(", ", comps));
        if (MessageBox.Show(L.Format(L.ChannelsView_PromoteTheCurrentBETAComponent, what), "Promote → RTM", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        var done = new List<string>();
        foreach (var c in comps)
        {
            try { await _api.PromoteAsync("beta", c, "rtm"); done.Add($"{c} ✓"); }
            catch (Exception ex) { done.Add($"{c} ✗ ({ex.Message})"); }
        }
        await RefreshAsync();
        _status.Text = "Promote — " + string.Join(" · ", done);
    }

    private async Task OpenMsiAsync()
    {
        try
        {
            var groups = await _api.GetGroupsAsync();
            _editorTitle = L.ChannelsView_BuildMSI_2;
            SetEditor(new MsiPanel(_api, groups));
            ShowEditor();
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private void OpenUpload()
    {
        _editorTitle = L.ChannelsView_ExeUpload;
        var panel = new UploadPanel(_api);
        panel.Uploaded += async () => { ShowMain(); await RefreshAsync(); };
        SetEditor(panel);
        ShowEditor();
    }

    private void SetEditor(Control panel)
    {
        _editorPanel?.Dispose();
        _editorPanel = panel;
        _editorContent.Controls.Clear();
        _editorContent.Controls.Add(panel);
    }
}
