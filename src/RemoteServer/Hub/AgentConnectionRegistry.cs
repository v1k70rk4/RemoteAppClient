using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteAgent.Commands;

namespace RemoteServer.Hub;

/// <summary>
/// Élő agent WSS-kapcsolatok in-memory regisztere: deviceId → socket.
/// Innen tud a szerver parancsot push-olni egy adott gépnek.
/// (Több szerver-instancia esetén ezt később egy backplane váltja — most egy instancia.)
/// </summary>
public sealed class AgentConnectionRegistry
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();

    public IReadOnlyCollection<string> ConnectedDevices => _connections.Keys.ToArray();

    public bool IsConnected(string deviceId) => _connections.ContainsKey(deviceId);

    public void Register(string deviceId, WebSocket socket) => _connections[deviceId] = socket;

    public void Unregister(string deviceId, WebSocket socket)
    {
        // Csak akkor távolítsuk el, ha még mindig EZ a socket (újracsatlakozás-biztos).
        if (_connections.TryGetValue(deviceId, out var current) && current == socket)
            _connections.TryRemove(deviceId, out _);
    }

    /// <summary>Aláírt parancs küldése egy gépnek. False, ha a gép nincs online.</summary>
    public async Task<bool> TrySendAsync(string deviceId, AgentCommand cmd, CancellationToken ct)
    {
        if (!_connections.TryGetValue(deviceId, out var socket) || socket.State != WebSocketState.Open)
            return false;

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(cmd, AgentJsonContext.Default.AgentCommand);
        await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct);
        return true;
    }
}
