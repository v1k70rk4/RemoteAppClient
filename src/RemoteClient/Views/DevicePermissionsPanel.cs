using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// Device Permissions tab: auto-update / BETA-channel toggles + a consent-required tri-state. Unattended
/// access is a reserved/unused flag, so it is not shown here. See design_handoff_console_redesign.
/// </summary>
public sealed class DevicePermissionsPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly string _deviceId;
    private readonly UiToggle _update = new();
    private readonly UiToggle _beta = new();
    private readonly Segment _consent = new(L.DeviceGeneralPanel_Inherited, L.Common_Yes, L.DeviceGeneralPanel_No_2);
    private readonly MaterialLabel _status = new() { AutoSize = true, Margin = new Padding(2, 10, 0, 0) };

    public DevicePermissionsPanel(AdminApi api, DeviceInfo d)
    {
        _api = api; _deviceId = d.DeviceId;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;
        Padding = new Padding(16);

        _update.Checked = d.UpdateAllowed;
        _beta.Checked = string.Equals(d.Channel, "beta", StringComparison.OrdinalIgnoreCase);
        _consent.SelectedIndex = d.ConsentRequired switch { null => 0, true => 1, false => 2 };

        var save = new UiButton(L.EditTokenForm_Save);
        save.Click += async (_, _) => await SaveAsync();

        const int cardW = 560, contentW = cardW - 36;
        var body = new Panel();
        body.Controls.Add(new SettingRow(L.DeviceGeneralPanel_UpdateAllowed, L.DevicePermissionsPanel_UpdateDesc, _update)
            { Location = new Point(0, 0), Size = new Size(contentW, 56) });
        body.Controls.Add(new SettingRow(L.DeviceGeneralPanel_BETAChannel, L.DevicePermissionsPanel_BetaDesc, _beta)
            { Location = new Point(0, 56), Size = new Size(contentW, 56) });
        _consent.Location = new Point(0, 148);
        save.Location = new Point(0, 202);
        _status.Location = new Point(2, 246);
        body.Controls.Add(_consent);
        body.Controls.Add(save);
        body.Controls.Add(_status);
        // "Consent required" caption sits above its segmented control (drawn on the card background).
        body.Paint += (_, e) => TextRenderer.DrawText(e.Graphics, L.DeviceGeneralPanel_ConsentRequired, UiFont.Body,
            new Rectangle(0, 124, contentW, 18), ThemeManager.Text, TextFormatFlags.Left | TextFormatFlags.NoPadding);

        Controls.Add(new Card(null, null, body) { Width = cardW, Height = 292, Location = new Point(16, 16) });
    }

    private async Task SaveAsync()
    {
        try
        {
            var upd = new DeviceUpdate
            {
                UpdateAllowed = _update.Checked,
                Channel = _beta.Checked ? "beta" : "rtm",
                ConsentRequired = _consent.SelectedIndex switch { 1 => true, 2 => false, _ => (bool?)null },
            };
            await _api.UpdateDeviceAsync(_deviceId, upd);
            _status.Text = L.Common_Saved;
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }
}
