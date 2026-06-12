using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient;

/// <summary>Egy felhasználó grantjainak kezelése: csoport- vagy gép-szintű hozzáférés ad/elvesz.</summary>
public sealed class GrantsForm : MaterialForm
{
    private readonly AdminApi _api;
    private readonly Guid _userId;
    private readonly ListView _list = new();
    private readonly MaterialComboBox _groups = new() { Hint = "Csoport" };
    private readonly MaterialComboBox _devices = new() { Hint = "Gép" };
    private readonly MaterialLabel _status = new();

    private sealed record GroupItem(Guid Id, string Name) { public override string ToString() => Name; }
    private sealed record DeviceItem(string DeviceId, string Name) { public override string ToString() => Name; }

    public GrantsForm(AdminApi api, Guid userId, string username)
    {
        _api = api; _userId = userId;
        ThemeManager.Skin.AddFormToManage(this);
        Text = $"Grantok — {username}";
        Sizable = false;
        Width = 580; Height = 520;
        StartPosition = FormStartPosition.CenterParent;

        _list.View = View.Details; _list.FullRowSelect = true; _list.Dock = DockStyle.Fill;
        _list.Columns.Add("Típus", 90);
        _list.Columns.Add("Név / Gép", 440);
        ThemeManager.StyleList(_list);

        var remove = new MaterialButton { Text = "Kijelölt elvétele", AutoSize = true, Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        remove.Click += async (_, _) => await RemoveSelectedAsync();
        var removeRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8, 8, 8, 0), WrapContents = false };
        removeRow.Controls.Add(remove);

        _groups.Width = 250;
        var addGroup = new MaterialButton { Text = "Csoport +", AutoSize = true, Margin = new Padding(10, 4, 4, 0) };
        addGroup.Click += async (_, _) => await AddGroupAsync();
        var groupRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(8, 6, 8, 0), WrapContents = false };
        groupRow.Controls.AddRange([_groups, addGroup]);

        _devices.Width = 250;
        var addDevice = new MaterialButton { Text = "Gép +", AutoSize = true, Margin = new Padding(10, 4, 4, 0) };
        addDevice.Click += async (_, _) => await AddDeviceAsync();
        var deviceRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(8, 6, 8, 0), WrapContents = false };
        deviceRow.Controls.AddRange([_devices, addDevice]);

        var bottom = new MaterialCard { Dock = DockStyle.Bottom, Height = 40, Margin = new Padding(0) };
        _status.Dock = DockStyle.Fill; _status.TextAlign = ContentAlignment.MiddleLeft; _status.Padding = new Padding(12, 0, 0, 0);
        bottom.Controls.Add(_status);

        // Dokk-sorrend: Fill először, majd a Bottom-sorok fentről lefelé (első Bottom = lista alá,
        // utolsó = legalsó szél), végül a Top-sor (legfelülre, a lista fölé).
        Controls.Add(_list);
        Controls.Add(groupRow);
        Controls.Add(deviceRow);
        Controls.Add(bottom);
        Controls.Add(removeRow);

        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            foreach (var g in await _api.GetGroupsAsync()) _groups.Items.Add(new GroupItem(g.Id, g.Name));
            if (_groups.Items.Count > 0) _groups.SelectedIndex = 0;
            foreach (var d in await _api.GetDevicesAsync())
                _devices.Items.Add(new DeviceItem(d.DeviceId, string.IsNullOrEmpty(d.Hostname) ? d.DeviceId : d.Hostname));
            if (_devices.Items.Count > 0) _devices.SelectedIndex = 0;
            await RefreshAsync();
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var grants = await _api.GetGrantsAsync(_userId);
            _list.Items.Clear();
            foreach (var g in grants)
            {
                var item = new ListViewItem(g.GroupId is not null ? "Csoport" : "Gép") { Tag = g };
                item.SubItems.Add(g.GroupId is not null ? (g.GroupName ?? g.GroupId.ToString()!) : (g.DeviceHostname ?? g.DeviceId ?? ""));
                _list.Items.Add(item);
            }
            _status.Text = $"{grants.Count} grant.";
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }

    private async Task AddGroupAsync()
    {
        if (_groups.SelectedItem is not GroupItem g) return;
        try { await _api.AddGrantAsync(_userId, g.Id, null); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }

    private async Task AddDeviceAsync()
    {
        if (_devices.SelectedItem is not DeviceItem d) return;
        try { await _api.AddGrantAsync(_userId, null, d.DeviceId); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }

    private async Task RemoveSelectedAsync()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not GrantInfo g) return;
        try { await _api.RemoveGrantAsync(_userId, g.Id); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }
}
