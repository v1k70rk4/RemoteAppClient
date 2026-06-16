using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using RemoteAgent.Security;

namespace RemoteAgent.Tunnel;

/// <summary>
/// Local TCP→WebSocket bridge for the <c>wss443</c> transport. Listens on a loopback port; when ssh
/// connects, it opens a <see cref="ClientWebSocket"/> to the server's <c>/ssh</c> endpoint (mTLS device
/// cert + pinned server cert, exactly like the C2 channel) and pipes bytes both ways. ssh therefore
/// reaches the bastion sshd "over HTTPS" — passing DPI / Cloudflare. The inner SSH still authenticates
/// with the bastion CA cert, so it is double-authenticated.
/// </summary>
public sealed class WsBridgeListener(string wssUrl, string pfxPath, string thumbprint, string pin, ILogger logger) : IAsyncDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public int LocalPort { get; private set; }

    /// <summary>Starts listening on a free loopback port and returns it. ssh connects here.</summary>
    public int Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(_cts.Token);
        return LocalPort;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await _listener!.AcceptTcpClientAsync(ct);
                _ = BridgeAsync(tcp, ct); // each ssh connection gets its own WebSocket
            }
        }
        catch { /* listener stopped */ }
    }

    private async Task BridgeAsync(TcpClient tcp, CancellationToken ct)
    {
        using (tcp)
        using (var ws = new ClientWebSocket())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(pfxPath) || !string.IsNullOrWhiteSpace(thumbprint))
                    ws.Options.ClientCertificates.Add(CertHelper.ResolveClientCertificate(pfxPath, thumbprint));
                if (!string.IsNullOrWhiteSpace(pin))
                    ws.Options.RemoteCertificateValidationCallback = CertHelper.PinnedServerValidator(pin);
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                await ws.ConnectAsync(new Uri(wssUrl), ct);
            }
            catch (Exception ex) { logger.LogWarning(ex, "ws-bridge: WSS connect to {Url} failed", wssUrl); return; }

            var stream = tcp.GetStream();
            var up = TcpToWsAsync(stream, ws, ct);
            var down = WsToTcpAsync(ws, stream, ct);
            await Task.WhenAny(up, down);
            try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { /* closing */ }
        }
    }

    private static async Task TcpToWsAsync(NetworkStream tcp, ClientWebSocket ws, CancellationToken ct)
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

    private static async Task WsToTcpAsync(ClientWebSocket ws, NetworkStream tcp, CancellationToken ct)
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

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { /* best effort */ }
        try { _listener?.Stop(); } catch { /* best effort */ }
        await Task.CompletedTask;
    }
}
