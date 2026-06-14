using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>User grants (group/device access), embedded in the user editor Permissions tab.</summary>
public sealed class GrantsPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly Guid _userId;
    private readonly ListView _list = new();
    private readonly MaterialComboBox _groups = new() { Hint = L.BootstrapView_Group };
    private readonly MaterialComboBox _devices = new() { Hint = L.DevicesView_Device };
    private readonly MaterialLabel _status = new();
    private bool _loaded;

    private sealed record GroupItem(Guid Id, string Name) { public override string ToString() => Name; }
    private sealed record DeviceItem(string DeviceId, string Name) { public override string ToString() => Name; }

    public GrantsPanel(AdminApi api, Guid userId)
    {
        _api = api; _userId = userId;
        Dock = DockStyle.Fill;

        _list.View = View.Details; _list.FullRowSelect = true; _list.Dock = DockStyle.Fill; _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add(L.BootstrapView_Type, 90);
        _list.Columns.Add(L.GrantsPanel_NameDevice, 440);

        var remove = new MaterialButton { Text = L.GrantsPanel_RemoveSelected, AutoSize = true, Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        remove.Click += async (_, _) => await RemoveSelectedAsync();
        // Below the table, aligned right.
        var removeRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8, 8, 8, 0), WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
        removeRow.Controls.Add(remove);

        _groups.Width = 250;
        var addGroup = new MaterialButton { Text = L.GrantsPanel_Group, AutoSize = true, Margin = new Padding(10, 4, 4, 0) };
        addGroup.Click += async (_, _) => await AddGroupAsync();
        var groupRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(8, 6, 8, 0), WrapContents = false };
        groupRow.Controls.AddRange([_groups, addGroup]);

        _devices.Width = 250;
        var addDevice = new MaterialButton { Text = L.GrantsPanel_Device, AutoSize = true, Margin = new Padding(10, 4, 4, 0) };
        addDevice.Click += async (_, _) => await AddDeviceAsync();
        var deviceRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(8, 6, 8, 0), WrapContents = false };
        deviceRow.Controls.AddRange([_devices, addDevice]);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 32 };
        _status.AutoSize = false; _status.Dock = DockStyle.Fill; _status.AutoEllipsis = true;
        _status.TextAlign = ContentAlignment.MiddleLeft; _status.Padding = new Padding(12, 0, 12, 0);
        bottom.Controls.Add(_status);

        // _list fills first; among Bottom rows, the first added (removeRow) sits highest,
        // directly below the list, and status is at the bottom.
        Controls.Add(_list);
        Controls.Add(removeRow);
        Controls.Add(groupRow);
        Controls.Add(deviceRow);
        Controls.Add(bottom);
    }

    /// <summary>Called when the tab opens: loads first time, refreshes afterwards.</summary>
    public async Task ShownAsync()
    {
        ThemeManager.StyleList(_list);
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
            _list.Items.Clear();
            foreach (var g in grants)
            {
                var item = new ListViewItem(g.GroupId is not null ? L.BootstrapView_Group : L.DevicesView_Device) { Tag = g };
                item.SubItems.Add(g.GroupId is not null ? (g.GroupName ?? g.GroupId.ToString()!) : (g.DeviceHostname ?? g.DeviceId ?? ""));
                _list.Items.Add(item);
            }
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
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not GrantInfo g) return;
        try { await _api.RemoveGrantAsync(_userId, g.Id); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }
}
