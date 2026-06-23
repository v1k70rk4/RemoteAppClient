using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>User grants (group/device access), embedded in the user editor Permissions tab: an add toolbar
/// (group + device) and an owner-drawn Type/Name table with Remove. See design_handoff_console_redesign.</summary>
public sealed class GrantsPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly Guid _userId;
    private readonly OwnerList _list = new(44);
    private readonly UiCombo _groups = new(240);
    private readonly UiCombo _devices = new(240);
    private readonly MaterialLabel _status = new() { Dock = DockStyle.Fill };
    private bool _loaded;

    private sealed record GroupItem(Guid Id, string Name) { public override string ToString() => Name; }
    private sealed record DeviceItem(string DeviceId, string Name) { public override string ToString() => Name; }

    public GrantsPanel(AdminApi api, Guid userId)
    {
        _api = api; _userId = userId;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;
        Padding = new Padding(22, 14, 22, 12);

        _list.Dock = DockStyle.Fill;
        _list.SetColumns(new OwnerList.Col(L.BootstrapView_Type, 110), new OwnerList.Col(L.GrantsPanel_NameDevice, 520));
        _list.PaintRow += (_, e) =>
        {
            var g = (GrantInfo)e.Item;
            bool isGroup = g.GroupId is not null;
            var (fg, bg) = isGroup ? (ThemeManager.Accent, ThemeManager.AccentSoft) : (ThemeManager.BetaFg, ThemeManager.BetaBg);
            UiPaint.DrawPill(e.G, e.Cell(0).Left, e.Cy, isGroup ? L.BootstrapView_Group : L.DevicesView_Device, fg, bg, UiFont.Label, false);
            e.Text(1, isGroup ? (g.GroupName ?? g.GroupId.ToString()!) : (g.DeviceHostname ?? g.DeviceId ?? ""), UiFont.Mono, ThemeManager.Text);
        };

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, WrapContents = false, BackColor = ThemeManager.Bg, Padding = new Padding(0, 8, 0, 0) };
        _groups.Margin = new Padding(0, 0, 8, 0);
        var addGroup = new UiButton(L.GrantsPanel_Group, UiButton.Style.Outline, "plus") { Margin = new Padding(0, 0, 20, 0) };
        addGroup.Click += async (_, _) => await AddGroupAsync();
        _devices.Margin = new Padding(0, 0, 8, 0);
        var addDevice = new UiButton(L.GrantsPanel_Device, UiButton.Style.Outline, "plus");
        addDevice.Click += async (_, _) => await AddDeviceAsync();
        toolbar.Controls.AddRange([_groups, addGroup, _devices, addDevice]);

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = ThemeManager.Bg };
        var remove = new UiButton(L.GrantsPanel_RemoveSelected, UiButton.Style.Warn) { Location = new Point(0, 6) };
        remove.Click += async (_, _) => await RemoveSelectedAsync();
        actions.Controls.Add(remove);

        var statusHost = new Panel { Dock = DockStyle.Bottom, Height = 22, BackColor = ThemeManager.Bg };
        statusHost.Controls.Add(_status);

        Controls.Add(_list);
        Controls.Add(actions);
        Controls.Add(statusHost);
        Controls.Add(toolbar);
    }

    public async Task ShownAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            try
            {
                foreach (var g in await _api.GetGroupsAsync()) _groups.Items.Add(new GroupItem(g.Id, g.Name));
                if (_groups.Items.Count > 0) _groups.SelectedIndex = 0;
                foreach (var d in await _api.GetDevicesAsync())
                    _devices.Items.Add(new DeviceItem(d.DeviceId, string.IsNullOrEmpty(d.Hostname) ? d.DeviceId : d.Hostname));
                if (_devices.Items.Count > 0) _devices.SelectedIndex = 0;
            }
            catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
        }
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var grants = await _api.GetGrantsAsync(_userId);
            _list.BeginUpdate();
            _list.Clear();
            foreach (var g in grants) _list.Add(g);
            _list.EndUpdate();
            _status.Text = $"{grants.Count} grant.";
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private async Task AddGroupAsync()
    {
        if (_groups.SelectedItem is not GroupItem g) return;
        try { await _api.AddGrantAsync(_userId, g.Id, null); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private async Task AddDeviceAsync()
    {
        if (_devices.SelectedItem is not DeviceItem d) return;
        try { await _api.AddGrantAsync(_userId, null, d.DeviceId); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private async Task RemoveSelectedAsync()
    {
        if (_list.Selected is not GrantInfo g) return;
        try { await _api.RemoveGrantAsync(_userId, g.Id); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }
}
