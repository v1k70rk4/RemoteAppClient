using System.Drawing;
using MaterialSkin;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>Read-only device telemetry/details. Used both in the editor Telemetry tab and in the
/// live VNC session panel, where <see cref="Update"/> re-renders it from a fresh snapshot.</summary>
public sealed class DeviceTelemetryPanel : UserControl
{
    private readonly FlowLayoutPanel _flow = new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
        AutoScroll = true, Padding = new Padding(12, 10, 8, 8),
    };
    private DeviceInfo? _last;

    public DeviceTelemetryPanel(DeviceInfo d)
    {
        Dock = DockStyle.Fill;
        Controls.Add(_flow);
        Build(d);
        _ = WarmLocalTransportAsync(); // fill the operator-side transport for the connect-path row
    }

    /// <summary>Re-renders all rows for a refreshed snapshot (used by the live session panel).</summary>
    public void Update(DeviceInfo d) => Build(d);

    private void Build(DeviceInfo d)
    {
        _last = d;
        _flow.SuspendLayout();
        _flow.Controls.Clear();

        void Row(string caption, string? value)
        {
            _flow.Controls.Add(new MaterialLabel { Text = caption, AutoSize = true, FontType = MaterialSkinManager.fontType.Caption, Margin = new Padding(0, 8, 0, 0) });
            _flow.Controls.Add(new MaterialLabel { Text = string.IsNullOrWhiteSpace(value) ? "—" : value, AutoSize = true, MaximumSize = new Size(420, 0) });
        }

        Row(L.DevicesView_Device, d.Hostname);
        Row("Online", d.Online ? "online" : "offline");
        Row(L.DeviceTelemetryPanel_LinkQuality, d.LinkFlaky ? L.Format(L.DeviceTelemetryPanel_LinkFlakyDetail, d.RecentReconnects) : L.DeviceTelemetryPanel_LinkStable);
        Row(L.DevicesView_LastOnline, d.LastSeenAt?.LocalDateTime.ToString("g"));
        Row(L.BootstrapView_Status, d.Status);
        Row(L.DeviceTelemetryPanel_Channel, string.Equals(d.Channel, "beta", StringComparison.OrdinalIgnoreCase) ? "BETA" : "rtm");
        Row(L.DeviceTelemetryPanel_SignedInUser, d.LoggedInUser ?? L.DeviceTelemetryPanel_No);
        Row(L.DeviceTelemetryPanel_IPAddressLocal, d.IpAddress);
        Row(L.DeviceTelemetryPanel_PublicIP, PublicIp(d));
        Row("Wi-Fi", string.IsNullOrWhiteSpace(d.WifiSsid) ? L.DeviceTelemetryPanel_WiredNoWiFi : d.WifiSsid);
        Row("VPN", d.VpnActive ? L.DeviceTelemetryPanel_Active : L.DeviceTelemetryPanel_No);
        Row(L.DeviceTelemetryPanel_BootTime, d.BootTimeUtc?.LocalDateTime.ToString("g"));
        Row(L.DeviceTelemetryPanel_Uptime, Uptime(d.BootTimeUtc));
        Row(L.DeviceTelemetryPanel_MakeModel, $"{(string.IsNullOrWhiteSpace(d.Manufacturer) ? "OEM" : d.Manufacturer)} / {S(d.Model)}");
        Row(L.DeviceTelemetryPanel_Serial, d.SerialNumber);
        Row(L.DeviceTelemetryPanel_LocalLock, d.VncLocked ? L.DeviceTelemetryPanel_DISABLED : "—");
        Row(L.DeviceTelemetryPanel_SignInLock, d.LoginLocked ? L.Format(L.DeviceTelemetryPanel_LOCKEDFailed, d.LoginFailCount) : (d.LoginFailCount > 0 ? L.Format(L.DeviceTelemetryPanel_FailedAttempt, d.LoginFailCount) : "—"));
        Row("Agent / Helper / VNC", $"{S(d.AgentVersion)} / {S(d.HelperVersion)} / {S(d.VncVersion)}");
        Row(L.DeviceTelemetryPanel_ClientOS, $"{S(d.ClientVersion)} / {S(d.OsVersion)}");
        Row(L.DeviceTelemetryPanel_AgentRestarts, d.AgentRestarts.ToString());
        if (!string.IsNullOrWhiteSpace(d.LastIncident)) Row(L.DeviceTelemetryPanel_LastIncident, d.LastIncident);
        Row("deviceId", d.DeviceId);
        Row(L.AboutView_Connection, ConnectPath(d));
        _flow.Controls.Add(new MaterialLabel { Text = " ", AutoSize = true, Margin = new Padding(0, 0, 0, 10) }); // trailing spacer so the last row is never clipped

        _flow.ResumeLayout();
    }

    private static string S(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v;

    /// <summary>Short transport label for the connect-path row.</summary>
    private static string TransportLabel(string? code) => (code ?? "auto").Trim().ToLowerInvariant() switch
    {
        "ssl443" => "443 (sslh)",
        "ssh22" => "22 (ssh)",
        "wss443" => "WSS",
        _ => "auto",
    };

    /// <summary>Operator transport ↔ Bastion ↔ device transport, e.g. "WSS ↔ Bastion ↔ WSS".</summary>
    private static string ConnectPath(DeviceInfo d)
    {
        var local = StatusClient.LastLocalTransport;
        var src = local is null ? "?" : TransportLabel(local);
        return $"{src} <-> Bastion <-> {TransportLabel(d.BastionTransport)}";
    }

    private async Task WarmLocalTransportAsync()
    {
        if (StatusClient.LastLocalTransport is not null) return; // already known from an earlier status query
        try { await StatusClient.QueryAgentAsync(); } catch { /* best effort */ }
        if (IsDisposed || _last is null) return;
        try { BeginInvoke(() => { if (!IsDisposed && _last is not null) Build(_last); }); } catch { /* form gone */ }
    }

    /// <summary>"reverse (ip)" when a PTR is cached, else just the IP, else "—". Shared with the device list.</summary>
    public static string PublicIp(DeviceInfo d) =>
        string.IsNullOrWhiteSpace(d.PublicIpAddress) ? "—"
        : string.IsNullOrWhiteSpace(d.PublicIpReverse) ? d.PublicIpAddress
        : $"{d.PublicIpReverse} ({d.PublicIpAddress})";

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
