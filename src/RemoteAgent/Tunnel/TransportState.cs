using System.Collections.Generic;
using System.Linq;

namespace RemoteAgent.Tunnel;

/// <summary>One bastion connection attempt: either raw SSH on a port, or SSH-over-WebSocket.</summary>
public readonly record struct TransportAttempt(bool Wss, int Port);

/// <summary>
/// Live bastion-transport state, shared between the telemetry push (writer of the configured
/// transport), the tunnels (readers that also record which attempt actually worked), and the status
/// pipe (reader for the About view). Thread-safe via volatile fields.
///
/// Transports: "auto" = 443 (sslh mux) → WSS fallback; "ssl443" = 443 only; "ssh22" = 22 only;
/// "wss443" = SSH-over-WebSocket only.
/// </summary>
public sealed class TransportState
{
    private volatile string _transport;
    private volatile int _lastWorking; // 0 = none yet, -1 = WSS, >0 = raw port
    private readonly string _wssUrl, _pfx, _thumb, _pin;

    public TransportState(string? initial, string wssUrl = "", string pfx = "", string thumb = "", string pin = "")
    {
        _transport = Normalize(initial);
        _wssUrl = wssUrl; _pfx = pfx; _thumb = thumb; _pin = pin;
    }

    /// <summary>Configured transport: "auto" | "ssl443" | "ssh22" | "wss443".</summary>
    public string Transport => _transport;

    /// <summary>WSS bridge parameters for a WSS attempt: the /ssh URL, device cert (pfx/thumbprint), server pin.</summary>
    public (string Url, string Pfx, string Thumb, string Pin) WssParams => (_wssUrl, _pfx, _thumb, _pin);

    /// <summary>Active bastion port for About/status: 443/22 = raw port, -1 = WSS, 0 = none yet.</summary>
    public int LastWorkingPort => _lastWorking;

    /// <summary>Applies a transport pushed by the server. No-op if unchanged or unrecognized.</summary>
    public void SetTransport(string? value)
    {
        var n = Normalize(value);
        if (n != _transport) { _transport = n; _lastWorking = 0; } // re-probe from the top under the new policy
    }

    public void RecordWorking(TransportAttempt a) => _lastWorking = a.Wss ? -1 : a.Port;

    /// <summary>
    /// Ordered connection attempts for the configured transport. The last-working attempt is tried
    /// first to avoid the fallback delay on every connect.
    /// </summary>
    public IReadOnlyList<TransportAttempt> Attempts(int fallbackPort)
    {
        static TransportAttempt Port(int p) => new(false, p);
        var wss = new TransportAttempt(true, 0);
        List<TransportAttempt> list = _transport switch
        {
            "ssh22" => [Port(fallbackPort)],
            "ssl443" => [Port(443)],
            "wss443" => [wss],
            _ => [Port(443), wss], // auto: 443 (sslh mux) → WSS fallback
        };
        int lw = _lastWorking;
        if (lw != 0)
        {
            int i = list.FindIndex(a => (a.Wss ? -1 : a.Port) == lw);
            if (i > 0) { var x = list[i]; list.RemoveAt(i); list.Insert(0, x); }
        }
        return list;
    }

    private static string Normalize(string? v) => v is "ssh22" or "ssl443" or "wss443" or "auto" ? v : "auto";
}
