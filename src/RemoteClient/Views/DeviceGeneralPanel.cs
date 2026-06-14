using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>Device General tab: group + note. Permissions/flags live on the Permissions tab.</summary>
public sealed class DeviceGeneralPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly string _deviceId;
    private readonly MaterialComboBox _group = new() { Width = 240 };
    private readonly MaterialMultiLineTextBox2 _note = new() { Width = 380, Height = 80 };
    private readonly MaterialLabel _status = new() { AutoSize = true, Margin = new Padding(4, 12, 0, 0) };

    private sealed record GroupItem(Guid? Id, string Name) { public override string ToString() => Name; }

    public DeviceGeneralPanel(AdminApi api, DeviceInfo d, List<GroupInfo> groups)
    {
        _api = api; _deviceId = d.DeviceId;
        Dock = DockStyle.Fill;

        _group.Items.Add(new GroupItem(null, L.DeviceGeneralPanel_No));
        foreach (var g in groups) _group.Items.Add(new GroupItem(g.Id, g.Name));
        _group.SelectedIndex = 0;
        for (int i = 0; i < _group.Items.Count; i++)
            if (_group.Items[i] is GroupItem gi && gi.Id == d.GroupId) { _group.SelectedIndex = i; break; }

        _note.Text = d.Note ?? "";

        var save = ViewUi.ToolbarButton(L.EditTokenForm_Save);
        save.Click += async (_, _) => await SaveAsync();

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 10, 12, 8) };
        void Lbl(string t) => body.Controls.Add(new MaterialLabel { Text = t, FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, Margin = new Padding(4, 10, 0, 0) });
        Lbl(L.BootstrapView_Group); body.Controls.Add(_group);
        Lbl(L.DeviceGeneralPanel_Note); body.Controls.Add(_note);
        body.Controls.Add(save);
        body.Controls.Add(_status);
        Controls.Add(body);
    }

    private async Task SaveAsync()
    {
        try
        {
            var upd = new DeviceUpdate
            {
                GroupId = ((GroupItem)_group.SelectedItem!).Id ?? Guid.Empty, // Empty makes the server set null
                Note = _note.Text,
            };
            await _api.UpdateDeviceAsync(_deviceId, upd);
            _status.Text = L.Common_Saved;
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }
}
