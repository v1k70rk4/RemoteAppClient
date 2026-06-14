using System.Net.WebSockets;
using System.Text.Json;
using RemoteAgent.Commands;

namespace RemoteAgent.Services;

/// <summary>
/// Sends messages back over the persistent C2 WSS channel (agent to server), such as tunnel
/// access results. CommandChannelService sets the current socket on connect; sending is
/// thread-safe with a single writer per socket. If there is no connection, the message is dropped.
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
        catch { /* connection is gone; next reconnect will fix it */ }
        finally { _gate.Release(); }
    }
}
