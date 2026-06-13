using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace RemoteAgent.Telemetry;

/// <summary>Hálózati telemetria: elsődleges IPv4, VPN-heurisztika, Wi-Fi SSID (netsh). Csak BCL + netsh, AOT-barát.</summary>
internal static class NetInfo
{
    private static readonly string[] VpnKeywords =
        ["vpn", "wireguard", "wintun", "openvpn", "tap-windows", "anyconnect", "fortinet", "forticlient",
         "globalprotect", "pangp", "zerotier", "tailscale", "nordlynx", "softether", "tunnel"];

    /// <summary>Az elsődleges (alapértelmezett átjáróval rendelkező) interfész IPv4 címe, vagy null.</summary>
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
        catch { /* nincs IP */ }
        return null;
    }

    /// <summary>Heurisztika: van-e aktív VPN-adapter (Ppp/Tunnel típus vagy ismert VPN-szoftver az adapter nevében).</summary>
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
        catch { /* nem tudjuk */ }
        return false;
    }

    /// <summary>A csatlakozott Wi-Fi SSID-je (netsh wlan show interfaces), vagy null, ha nincs/nem csatlakozik.</summary>
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
                if (key != "ssid") continue;              // a "BSSID" nem egyezik (exact match)
                var val = raw[(i + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
        }
        catch { /* nincs WLAN / hiba */ }
        return null;
    }
}
