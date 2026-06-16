using System.Collections.Generic;
using System.Linq;

namespace RemoteAgent.Tunnel;

/// <summary>
/// Live bastion-transport state, shared between the telemetry push (writer of the configured
/// transport), the tunnels (readers that also record which port actually worked), and the status
/// pipe (reader for the About view). Thread-safe via volatile fields.
/// </summary>
public sealed class TransportState
{
    private volatile string _transport;
    private volatile int _lastWorkingPort; // 443 or the fallback port; 0 = none yet

    public TransportState(string? initial) => _transport = Normalize(initial);

    /// <summary>Configured transport: "auto" | "ssl443" | "ssh22" | "wss443".</summary>
    public string Transport => _transport;

    /// <summary>Bastion port the last tunnel actually connected on, shown in About. 0 = none yet.</summary>
    public int LastWorkingPort => _lastWorkingPort;

    /// <summary>Applies a transport pushed by the server. No-op if unchanged or unrecognized.</summary>
    public void SetTransport(string? value)
    {
        var n = Normalize(value);
        if (n != _transport)
        {
            _transport = n;
            _lastWorkingPort = 0; // re-probe from the top under the new policy
        }
    }

    public void RecordWorkingPort(int port) => _lastWorkingPort = port;

    /// <summary>
    /// Ordered bastion ports to try for the configured transport. 443 is the sslh port; the fallback
    /// port (normally 22) comes from config. The last-working port is tried first to avoid the
    /// fallback delay on every connect.
    /// </summary>
    public IReadOnlyList<int> CandidatePorts(int fallbackPort)
    {
        IEnumerable<int> order = _transport switch
        {
            "ssh22" => new[] { fallbackPort },
            "ssl443" => new[] { 443 },
            _ => new[] { 443, fallbackPort }, // auto, and wss443 until that transport exists
        };
        var list = order.Distinct().ToList();
        int last = _lastWorkingPort;
        if (last != 0 && list.Contains(last))
            list = list.Where(p => p == last).Concat(list.Where(p => p != last)).ToList();
        return list;
    }

    private static string Normalize(string? v) => v is "ssh22" or "ssl443" or "wss443" or "auto" ? v : "auto";
}
