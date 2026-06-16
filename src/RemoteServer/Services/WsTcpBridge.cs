using System.Net.Sockets;
using System.Net.WebSockets;

namespace RemoteServer.Services;

/// <summary>
/// Pipes bytes both ways between a WebSocket (binary frames) and a TCP stream until either side
/// closes. Used by the /ssh endpoint to bridge a device's SSH-over-WSS tunnel to the local bastion sshd.
/// </summary>
public static class WsTcpBridge
{
    public static async Task RunAsync(WebSocket ws, NetworkStream tcp, CancellationToken ct)
    {
        var a = WsToTcpAsync(ws, tcp, ct);
        var b = TcpToWsAsync(tcp, ws, ct);
        await Task.WhenAny(a, b);
    }

    private static async Task WsToTcpAsync(WebSocket ws, NetworkStream tcp, CancellationToken ct)
    {
        var buf = new byte[16384];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.Count > 0) await tcp.WriteAsync(buf.AsMemory(0, r.Count), ct);
            }
        }
        catch { /* peer closed */ }
    }

    private static async Task TcpToWsAsync(NetworkStream tcp, WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16384];
        try
        {
            int n;
            while ((n = await tcp.ReadAsync(buf, ct)) > 0 && ws.State == WebSocketState.Open)
                await ws.SendAsync(buf.AsMemory(0, n), WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
        catch { /* peer closed */ }
    }
}
