using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// BETA tab, shown only for beta-channel devices: experimental per-device settings. Currently the bastion
/// transport selector — how the agent reaches the bastion (443 sslh / 22 ssh). See design_handoff.
/// </summary>
public sealed class DeviceBetaPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly string _deviceId;
    private readonly UiCombo _transport = new(280);
    private readonly MaterialLabel _status = new() { AutoSize = true, Margin = new Padding(2, 10, 0, 0) };

    private sealed record TransportItem(string Code, string Label) { public override string ToString() => Label; }

    public DeviceBetaPanel(AdminApi api, DeviceInfo d)
    {
        _api = api; _deviceId = d.DeviceId;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;
        Padding = new Padding(16);

        _transport.Items.Add(new TransportItem("auto", L.DeviceBetaPanel_TransportAuto));
        _transport.Items.Add(new TransportItem("ssl443", L.DeviceBetaPanel_TransportSsl));
        _transport.Items.Add(new TransportItem("ssh22", L.DeviceBetaPanel_TransportSsh));
        _transport.Items.Add(new TransportItem("wss443", L.DeviceBetaPanel_TransportWss));
        var cur = (d.BastionTransport ?? "auto").Trim().ToLowerInvariant();
        _transport.SelectedIndex = 0;
        for (int i = 0; i < _transport.Items.Count; i++)
            if (_transport.Items[i] is TransportItem ti && ti.Code == cur) { _transport.SelectedIndex = i; break; }

        var save = new UiButton(L.EditTokenForm_Save);
        save.Click += async (_, _) => await SaveAsync();

        const int cardW = 520;
        var body = new Panel();
        _transport.Location = new Point(0, 2);
        save.Location = new Point(0, 58);
        _status.Location = new Point(0, 104);
        body.Controls.Add(_transport);
        body.Controls.Add(save);
        body.Controls.Add(_status);
        Controls.Add(new Card(L.DeviceBetaPanel_Transport, L.DeviceBetaPanel_TransportDesc, body)
            { Width = cardW, Height = 66 + 132 + 16, Location = new Point(16, 16) });
    }

    private async Task SaveAsync()
    {
        try
        {
            var code = (_transport.SelectedItem as TransportItem)?.Code ?? "auto";
            await _api.UpdateDeviceAsync(_deviceId, new DeviceUpdate { BastionTransport = code });
            _status.Text = L.Common_Saved;
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }
}
