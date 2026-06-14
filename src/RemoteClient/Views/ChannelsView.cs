using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// Release channels in two tables (RTM | BETA), with current version per component and a "Released"
/// flag indicating whether all updatable approved devices on the channel have the package version.
/// Rollout/Promote sit below the tables. EXE upload and MSI build open as in-window editors.
/// </summary>
public sealed class ChannelsView : UserControl, IContentView
{
    private readonly AdminApi _api;
    private readonly Panel _mainHost = new() { Dock = DockStyle.Fill };
    private readonly Panel _editorHost = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly ListView _rtmList = NewList();
    private readonly ListView _betaList = NewList();
    private readonly MaterialLabel _status = new();

    private readonly MaterialLabel _editorTitle = new() { Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Margin = new Padding(12, 10, 0, 0) };
    private readonly Panel _editorContent = new() { Dock = DockStyle.Fill };
    private Control? _editorPanel;

    public ChannelsView(AdminApi api)
    {
        _api = api;
        Dock = DockStyle.Fill;
        BuildMain();
        BuildEditor();
        Controls.Add(_editorHost);
        Controls.Add(_mainHost);
        ApplyTheme();
    }

    private void BuildMain()
    {
        var rtmBtns = ViewUi.Toolbar();
        var rolloutRtm = ViewUi.ToolbarButton("Rollout RTM");
        rolloutRtm.Click += async (_, _) => await RolloutChannelAsync("rtm", _rtmList);
        rtmBtns.Controls.Add(rolloutRtm);

        var betaBtns = ViewUi.Toolbar();
        var rolloutBeta = ViewUi.ToolbarButton("Rollout BETA");
        rolloutBeta.Click += async (_, _) => await RolloutChannelAsync("beta", _betaList);
        var promote = ViewUi.ToolbarButton("Promote → RTM", primary: false);
        promote.Click += async (_, _) => await PromoteChannelAsync();
        betaBtns.Controls.AddRange([rolloutBeta, promote]);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.Controls.Add(ChannelCard("RTM", _rtmList, rtmBtns), 0, 0);
        grid.Controls.Add(ChannelCard("BETA", _betaList, betaBtns), 1, 0);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8, 4, 8, 4) };
        var upload = ViewUi.ToolbarButton(L.ChannelsView_001, primary: false); upload.Margin = new Padding(4, 0, 8, 0);
        upload.Click += (_, _) => OpenUpload();
        var msi = ViewUi.ToolbarButton(L.ChannelsView_002, primary: false); msi.Margin = new Padding(4, 0, 8, 0);
        msi.Click += async (_, _) => await OpenMsiAsync();
        bottom.Controls.AddRange([upload, msi]);

        _mainHost.Controls.Add(ViewUi.Rows(0, grid, bottom, ViewUi.StatusHost(_status)));
    }

    private void BuildEditor()
    {
        var back = ViewUi.ToolbarButton(L.ChannelsView_013, primary: false);
        back.Click += async (_, _) => { ShowMain(); await RefreshAsync(); };
        var general = new MaterialButton { Text = L.ChannelsView_003, AutoSize = true, Margin = new Padding(4, 0, 0, 0), Type = MaterialButton.MaterialButtonType.Contained };
        var tabbar = ViewUi.Toolbar();
        tabbar.Controls.AddRange([back, general]);
        _editorHost.Controls.Add(ViewUi.Rows(2, tabbar, _editorTitle, _editorContent));
    }

    private static ListView NewList()
    {
        var l = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill };
        l.Columns.Add(L.ChannelsView_014, 85);
        l.Columns.Add(L.ChannelsView_004, 65);
        l.Columns.Add(L.ChannelsView_005, 125);
        l.Columns.Add(L.ChannelsView_015, 50);
        return l;
    }

    private static MaterialCard ChannelCard(string title, ListView list, FlowLayoutPanel buttons)
    {
        var card = new MaterialCard { Dock = DockStyle.Fill, Margin = new Padding(6), Padding = new Padding(0) };
        var head = new MaterialLabel { Text = title, Font = new Font("Segoe UI", 12F, FontStyle.Bold), Dock = DockStyle.Top, Height = 30, Padding = new Padding(12, 6, 0, 0) };
        buttons.Dock = DockStyle.Bottom; buttons.AutoSize = true; buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        card.Controls.Add(list);
        card.Controls.Add(buttons);
        card.Controls.Add(head);
        return card;
    }

    public void ApplyTheme() { ThemeManager.StyleView(this, _rtmList); ThemeManager.StyleList(_betaList); }

    public async Task OnShownAsync() { ShowMain(); await RefreshAsync(); }

    private void ShowMain() { _editorHost.Visible = false; _mainHost.Visible = true; _mainHost.BringToFront(); }
    private void ShowEditor() { _mainHost.Visible = false; _editorHost.Visible = true; _editorHost.BringToFront(); }

    private async Task RefreshAsync()
    {
        try
        {
            var ch = await _api.GetChannelsAsync();
            var devices = await _api.GetDevicesAsync();
            Fill(_rtmList, ch.Where(p => string.Equals(p.Channel, "rtm", StringComparison.OrdinalIgnoreCase)), devices);
            Fill(_betaList, ch.Where(p => string.Equals(p.Channel, "beta", StringComparison.OrdinalIgnoreCase)), devices);
            _status.Text = ch.Count == 0 ? L.ChannelsView_006 : L.Format(L.ChannelsView_016, _rtmList.Items.Count, _betaList.Items.Count);
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_019 + ex.Message; }
    }

    private static void Fill(ListView list, IEnumerable<ChannelPackageInfo> items, List<DeviceInfo> devices)
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var p in items.OrderBy(p => p.Component))
        {
            var it = new ListViewItem(p.Component) { Tag = p, UseItemStyleForSubItems = false };
            it.SubItems.Add(p.Version);
            it.SubItems.Add(p.UploadedAt.LocalDateTime.ToString("yyyy.MM.dd HH:mm"));
            var rolled = it.SubItems.Add(RolledOut(p.Channel, p.Component, p.Version, devices) ? "✓" : "—");
            rolled.ForeColor = rolled.Text == "✓" ? Color.MediumSeaGreen : Color.Gray;
            list.Items.Add(it);
        }
        list.EndUpdate();
    }

    /// <summary>Whether all updatable approved devices on the channel are on the package version (= released).</summary>
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

    private static string? SelectedComp(ListView list) =>
        list.SelectedItems.Count == 0 ? null : ((ChannelPackageInfo)list.SelectedItems[0].Tag!).Component;

    private static string[] AllComps(ListView list) =>
        list.Items.Cast<ListViewItem>().Select(i => ((ChannelPackageInfo)i.Tag!).Component).ToArray();

    private async Task RolloutChannelAsync(string channel, ListView list)
    {
        var sel = SelectedComp(list);
        var comps = sel is not null ? [sel] : AllComps(list);
        if (comps.Length == 0) { _status.Text = L.ChannelsView_007; return; }
        var what = sel is not null
            ? L.Format(L.ChannelsView_017, sel)
            : L.Format(L.ChannelsView_018, string.Join(", ", comps));
        if (MessageBox.Show(L.Format(L.ChannelsView_008, channel.ToUpperInvariant(), what),
                "Rollout", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

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
        var comps = sel is not null ? [sel] : AllComps(_betaList);
        if (comps.Length == 0) { _status.Text = L.ChannelsView_009; return; }
        var what = sel is not null
            ? L.Format(L.ChannelsView_017, sel)
            : L.Format(L.ChannelsView_018, string.Join(", ", comps));
        if (MessageBox.Show(L.Format(L.ChannelsView_010, what),
                "Promote → RTM", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

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
            _editorTitle.Text = L.ChannelsView_011;
            SetEditor(new MsiPanel(_api, groups));
            ShowEditor();
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_019 + ex.Message; }
    }

    private void OpenUpload()
    {
        _editorTitle.Text = L.ChannelsView_012;
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
