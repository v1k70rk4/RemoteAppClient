using System.Net.WebSockets;
using System.Text.Json;
using RemoteAgent.Commands;

namespace RemoteAgent.Services;

/// <summary>
/// Visszafelé küldés a perzisztens C2 WSS-en (agent → szerver), pl. a tunnel-nyitás/hozzájárulás
/// eredménye. A CommandChannelService állítja be az aktuális socketet csatlakozáskor; a küldés
/// szálbiztos (egy socketre egyszerre egy író). Ha épp nincs kapcsolat, az üzenet csendben elvész.
/// </summary>
public sealed class AgentUplink
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebSocket? _socket;

    public void SetSocket(WebSocket socket) => _socket = socket;
    public void Clear(WebSocket socket) { if (ReferenceEquals(_socket, socket)) _socket = null; }

    public async Task ReportAccessResultAsync(string nonce, string outcome, CancellationToken ct = default)
    {
        var msg = new AgentUplinkMessage { Type = "access-result", Nonce = nonce, Outcome = outcome };
        await SendAsync(JsonSerializer.SerializeToUtf8Bytes(msg, AgentJsonContext.Default.AgentUplinkMessage), ct);
    }

    private async Task SendAsync(byte[] payload, CancellationToken ct)
    {
        var sock = _socket;
        if (sock is null || sock.State != WebSocketState.Open) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_socket is { State: WebSocketState.Open } s)
                await s.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch { /* a kapcsolat elment — a következő reconnect rendezi */ }
        finally { _gate.Release(); }
    }
}
