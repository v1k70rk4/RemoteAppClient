using RemoteAgent.Admin;

namespace RemoteClient;

/// <summary>Egy felhasználó grantjainak kezelése: csoport- vagy gép-szintű hozzáférés ad/elvesz.</summary>
public sealed class GrantsForm : Form
{
    private readonly AdminApi _api;
    private readonly Guid _userId;
    private readonly ListView _list = new();
    private readonly ComboBox _groups = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _devices = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _status = new();

    private sealed record GroupItem(Guid Id, string Name) { public override string ToString() => Name; }
    private sealed record DeviceItem(string DeviceId, string Name) { public override string ToString() => Name; }

    public GrantsForm(AdminApi api, Guid userId, string username)
    {
        _api = api; _userId = userId;
        Text = $"Grantok — {username}";
        Width = 560; Height = 420;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        _list.View = View.Details; _list.FullRowSelect = true; _list.Dock = DockStyle.Top; _list.Height = 200;
        _list.Columns.Add("Típus", 90);
        _list.Columns.Add("Név / Gép", 420);

        var remove = new Button { Text = "Kijelölt elvétele", Bounds = new Rectangle(12, 210, 150, 30) };
        remove.Click += async (_, _) => await RemoveSelectedAsync();

        AddLabel("Csoport:", 12, 256);
        _groups.SetBounds(90, 252, 250, 24);
        var addGroup = new Button { Text = "Csoport +", Bounds = new Rectangle(350, 250, 110, 28) };
        addGroup.Click += async (_, _) => await AddGroupAsync();

        AddLabel("Gép:", 12, 292);
        _devices.SetBounds(90, 288, 250, 24);
        var addDevice = new Button { Text = "Gép +", Bounds = new Rectangle(350, 286, 110, 28) };
        addDevice.Click += async (_, _) => await AddDeviceAsync();

        _status.SetBounds(12, 330, 530, 40);

        Controls.AddRange([_list, remove, _groups, addGroup, _devices, addDevice, _status]);
        Load += async (_, _) => await InitAsync();
    }

    private void AddLabel(string t, int x, int y) => Controls.Add(new Label { Text = t, Bounds = new Rectangle(x, y + 3, 76, 22) });

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
