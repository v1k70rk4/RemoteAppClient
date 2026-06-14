using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>Device Permissions tab: updatable / BETA channel / unattended access / consent required.</summary>
public sealed class DevicePermissionsPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly string _deviceId;
    private readonly MaterialSwitch _update = new() { Text = L.DeviceGeneralPanel_UpdateAllowed, AutoSize = true };
    private readonly MaterialSwitch _beta = new() { Text = L.DeviceGeneralPanel_BETAChannel, AutoSize = true };
    private readonly MaterialComboBox _unattended = new() { Hint = L.DeviceGeneralPanel_UnattendedAccess, Width = 240 };
    private readonly MaterialComboBox _consent = new() { Hint = L.DeviceGeneralPanel_ConsentRequired, Width = 240 };
    private readonly MaterialLabel _status = new() { AutoSize = true, Margin = new Padding(4, 12, 0, 0) };

    public DevicePermissionsPanel(AdminApi api, DeviceInfo d)
    {
        _api = api; _deviceId = d.DeviceId;
        Dock = DockStyle.Fill;

        _update.Checked = d.UpdateAllowed;
        _beta.Checked = string.Equals(d.Channel, "beta", StringComparison.OrdinalIgnoreCase);
        SetupTri(_unattended, d.UnattendedAllowed);
        SetupTri(_consent, d.ConsentRequired);

        var save = ViewUi.ToolbarButton(L.EditTokenForm_Save);
        save.Click += async (_, _) => await SaveAsync();

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 10, 12, 8) };
        void Lbl(string t) => body.Controls.Add(new MaterialLabel { Text = t, FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, Margin = new Padding(4, 10, 0, 0) });
        body.Controls.Add(_update);
        body.Controls.Add(_beta);
        Lbl(L.DeviceGeneralPanel_UnattendedAccess_2); body.Controls.Add(_unattended);
        Lbl(L.DeviceGeneralPanel_ConsentRequired_2); body.Controls.Add(_consent);
        body.Controls.Add(save);
        body.Controls.Add(_status);
        Controls.Add(body);
    }

    private static void SetupTri(MaterialComboBox combo, bool? value)
    {
        combo.Items.AddRange([L.DeviceGeneralPanel_Inherited, L.Common_Yes, L.DeviceGeneralPanel_No_2]);
        combo.SelectedIndex = value switch { null => 0, true => 1, false => 2 };
    }

    private static bool? FromTri(MaterialComboBox combo) => combo.SelectedIndex switch { 1 => true, 2 => false, _ => null };

    private async Task SaveAsync()
    {
        try
        {
            var upd = new DeviceUpdate
            {
                UpdateAllowed = _update.Checked,
                Channel = _beta.Checked ? "beta" : "rtm",
                UnattendedAllowed = FromTri(_unattended),
                ConsentRequired = FromTri(_consent),
            };
            await _api.UpdateDeviceAsync(_deviceId, upd);
            _status.Text = L.Common_Saved;
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }
}
