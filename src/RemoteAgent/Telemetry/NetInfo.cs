using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace RemoteAgent.Telemetry;

/// <summary>Network telemetry: primary IPv4, VPN heuristic, and Wi-Fi SSID via netsh. BCL + netsh only, AOT-friendly.</summary>
internal static class NetInfo
{
    private static readonly string[] VpnKeywords =
        ["vpn", "wireguard", "wintun", "openvpn", "tap-windows", "anyconnect", "fortinet", "forticlient",
         "globalprotect", "pangp", "zerotier", "tailscale", "nordlynx", "softether", "tunnel"];

    /// <summary>IPv4 address of the primary interface with a default gateway, or null.</summary>
    public static string? PrimaryIPv4()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var p = ni.GetIPProperties();
                bool hasGw = p.GatewayAddresses.Any(g => g.Address?.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                if (!hasGw) continue;
                var ip = p.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                if (ip is not null) return ip.ToString();
            }
        }
        catch { /* no IP */ }
        return null;
    }

    /// <summary>Heuristic for active VPN adapters: Ppp/Tunnel type or known VPN software in the adapter name.</summary>
    public static bool IsVpnActive()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel) return true;
                var name = (ni.Name + " " + ni.Description).ToLowerInvariant();
                if (VpnKeywords.Any(name.Contains)) return true;
            }
        }
        catch { /* unknown */ }
        return false;
    }

    /// <summary>Connected Wi-Fi SSID from netsh wlan show interfaces, or null when absent/disconnected.</summary>
    public static string? WifiSsid()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } return null; }

            foreach (var raw in outp.Split('\n'))
            {
                int i = raw.IndexOf(':');
                if (i < 0) continue;
                var key = raw[..i].Trim().ToLowerInvariant();
                if (key != "ssid") continue;              // "BSSID" does not match because this is exact
                var val = raw[(i + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
        }
        catch { /* no WLAN / error */ }
        return null;
    }
}
