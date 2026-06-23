using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>Device General tab: group + note in a title-less card (per design_handoff_console_redesign).
/// Permissions/flags live on the Permissions tab.</summary>
public sealed class DeviceGeneralPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly string _deviceId;
    private readonly UiCombo _group = new(260);
    private readonly TextField _note = new("", 380, multiline: true);
    private readonly MaterialLabel _status = new() { AutoSize = true, Margin = new Padding(2, 10, 0, 0) };

    private sealed record GroupItem(Guid? Id, string Name) { public override string ToString() => Name; }

    public DeviceGeneralPanel(AdminApi api, DeviceInfo d, List<GroupInfo> groups)
    {
        _api = api; _deviceId = d.DeviceId;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;
        Padding = new Padding(16);

        _group.Items.Add(new GroupItem(null, L.DeviceGeneralPanel_No));
        foreach (var g in groups) _group.Items.Add(new GroupItem(g.Id, g.Name));
        _group.SelectedIndex = 0;
        for (int i = 0; i < _group.Items.Count; i++)
            if (_group.Items[i] is GroupItem gi && gi.Id == d.GroupId) { _group.SelectedIndex = i; break; }

        _note.Text = d.Note ?? "";

        var save = new UiButton(L.EditTokenForm_Save);
        save.Click += async (_, _) => await SaveAsync();

        const int cardW = 540, contentW = cardW - 36;
        var body = new Panel();
        _group.Location = new Point(0, 24);
        _note.SetBounds(0, 102, contentW, 84);
        save.Location = new Point(0, 198);
        _status.Location = new Point(2, 240);
        body.Controls.Add(_group);
        body.Controls.Add(_note);
        body.Controls.Add(save);
        body.Controls.Add(_status);
        // field captions drawn on the card background, above each input
        body.Paint += (_, e) =>
        {
            TextRenderer.DrawText(e.Graphics, L.BootstrapView_Group, UiFont.Label, new Rectangle(0, 2, contentW, 16),
                ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, L.DeviceGeneralPanel_Note, UiFont.Label, new Rectangle(0, 82, contentW, 16),
                ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding);
        };

        Controls.Add(new Card(null, null, body) { Width = cardW, Height = 300, Location = new Point(16, 16) });
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
