using System.Drawing;
using MaterialSkin;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>Read-only device telemetry/details for the editor Telemetry tab.</summary>
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

        Row(L.DevicesView_Device, d.Hostname);
        Row("Online", d.Online ? "online" : "offline");
        Row(L.DevicesView_LastOnline, d.LastSeenAt?.LocalDateTime.ToString("g"));
        Row(L.BootstrapView_Status, d.Status);
        Row(L.DeviceTelemetryPanel_Channel, string.Equals(d.Channel, "beta", StringComparison.OrdinalIgnoreCase) ? "BETA" : "rtm");
        Row(L.DeviceTelemetryPanel_SignedInUser, d.LoggedInUser ?? L.DeviceTelemetryPanel_No);
        Row(L.DeviceTelemetryPanel_IPAddressLocal, d.IpAddress);
        Row(L.DeviceTelemetryPanel_PublicIP, d.PublicIpAddress);
        Row("Wi-Fi", string.IsNullOrWhiteSpace(d.WifiSsid) ? L.DeviceTelemetryPanel_WiredNoWiFi : d.WifiSsid);
        Row("VPN", d.VpnActive ? L.DeviceTelemetryPanel_Active : L.DeviceTelemetryPanel_No);
        Row(L.DeviceTelemetryPanel_BootTime, d.BootTimeUtc?.LocalDateTime.ToString("g"));
        Row(L.DeviceTelemetryPanel_Uptime, Uptime(d.BootTimeUtc));
        Row(L.DeviceTelemetryPanel_LocalLock, d.VncLocked ? L.DeviceTelemetryPanel_DISABLED : "—");
        Row(L.DeviceTelemetryPanel_SignInLock, d.LoginLocked ? L.Format(L.DeviceTelemetryPanel_LOCKEDFailed, d.LoginFailCount) : (d.LoginFailCount > 0 ? L.Format(L.DeviceTelemetryPanel_FailedAttempt, d.LoginFailCount) : "—"));
        Row("Agent / Helper / VNC", $"{S(d.AgentVersion)} / {S(d.HelperVersion)} / {S(d.VncVersion)}");
        Row(L.DeviceTelemetryPanel_ClientOS, $"{S(d.ClientVersion)} / {S(d.OsVersion)}");
        Row(L.DeviceTelemetryPanel_AgentRestarts, d.AgentRestarts.ToString());
        if (!string.IsNullOrWhiteSpace(d.LastIncident)) Row(L.DeviceTelemetryPanel_LastIncident, d.LastIncident);
        Row("deviceId", d.DeviceId);

        Controls.Add(flow);
    }

    private static string S(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v;

    private static string? Uptime(DateTimeOffset? boot)
    {
        if (boot is not { } b || b == default) return null;
        var t = DateTimeOffset.UtcNow - b;
        if (t < TimeSpan.Zero) return null;
        if (t.TotalDays >= 1) return L.Format(L.DeviceTelemetryPanel_DayHour, (int)t.TotalDays, t.Hours);
        if (t.TotalHours >= 1) return L.Format(L.DeviceTelemetryPanel_HourMinute, (int)t.TotalHours, t.Minutes);
        return $"{t.Minutes} perc";
    }
}
