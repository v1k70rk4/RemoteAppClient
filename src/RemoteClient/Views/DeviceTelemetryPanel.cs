using System.Drawing;
using MaterialSkin;
using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient.Views;

/// <summary>Egy eszköz telemetriája/részletei (csak olvasható) — a szerkesztő „Telemetria" füle.</summary>
public sealed class DeviceTelemetryPanel : UserControl
{
    public DeviceTelemetryPanel(DeviceInfo d)
    {
        Dock = DockStyle.Fill;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 10, 8, 8) };

        void Row(string caption, string? value)
        {
            flow.Controls.Add(new MaterialLabel { Text = caption, AutoSize = true, FontType = MaterialSkinManager.fontType.Caption, Margin = new Padding(0, 8, 0, 0) });
            flow.Controls.Add(new MaterialLabel { Text = string.IsNullOrWhiteSpace(value) ? "—" : value, AutoSize = true, MaximumSize = new Size(420, 0) });
        }

        Row("Gép", d.Hostname);
        Row("Online", d.Online ? "online" : "offline");
        Row("Utoljára online", d.LastSeenAt?.LocalDateTime.ToString("g"));
        Row("Állapot", d.Status);
        Row("Csatorna", string.Equals(d.Channel, "beta", StringComparison.OrdinalIgnoreCase) ? "BETA" : "rtm");
        Row("Helyi zár", d.VncLocked ? "LETILTVA" : "—");
        Row("Agent / Helper / VNC", $"{S(d.AgentVersion)} / {S(d.HelperVersion)} / {S(d.VncVersion)}");
        Row("Kliens / OS", $"{S(d.ClientVersion)} / {S(d.OsVersion)}");
        Row("Agent-restartok", d.AgentRestarts.ToString());
        if (!string.IsNullOrWhiteSpace(d.LastIncident)) Row("Utolsó incidens", d.LastIncident);
        Row("deviceId", d.DeviceId);

        Controls.Add(flow);
    }

    private static string S(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v;
}
