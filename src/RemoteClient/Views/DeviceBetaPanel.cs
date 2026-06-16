using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// BETA tab, shown only for beta-channel devices: experimental per-device settings.
/// Currently the bastion transport selector — how the agent reaches the bastion (443 sslh / 22 ssh).
/// </summary>
public sealed class DeviceBetaPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly string _deviceId;
    private readonly MaterialComboBox _transport = new() { Width = 260 };
    private readonly MaterialLabel _status = new() { AutoSize = true, Margin = new Padding(4, 12, 0, 0) };

    private sealed record TransportItem(string Code, string Label) { public override string ToString() => Label; }

    public DeviceBetaPanel(AdminApi api, DeviceInfo d)
    {
        _api = api; _deviceId = d.DeviceId;
        Dock = DockStyle.Fill;

        _transport.Items.Add(new TransportItem("auto", L.DeviceBetaPanel_TransportAuto));
        _transport.Items.Add(new TransportItem("ssl443", L.DeviceBetaPanel_TransportSsl));
        _transport.Items.Add(new TransportItem("ssh22", L.DeviceBetaPanel_TransportSsh));
        _transport.Items.Add(new TransportItem("wss443", L.DeviceBetaPanel_TransportWss));
        var cur = (d.BastionTransport ?? "auto").Trim().ToLowerInvariant();
        _transport.SelectedIndex = 0;
        for (int i = 0; i < _transport.Items.Count; i++)
            if (_transport.Items[i] is TransportItem ti && ti.Code == cur) { _transport.SelectedIndex = i; break; }

        var save = ViewUi.ToolbarButton(L.EditTokenForm_Save);
        save.Click += async (_, _) => await SaveAsync();

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 10, 12, 8) };
        void Lbl(string t) => body.Controls.Add(new MaterialLabel { Text = t, FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, Margin = new Padding(4, 10, 0, 0) });
        Lbl(L.DeviceBetaPanel_Transport);
        body.Controls.Add(_transport);
        body.Controls.Add(save);
        body.Controls.Add(_status);
        Controls.Add(body);
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
