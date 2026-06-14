using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteAgent.Commands;

namespace RemoteServer.Hub;

/// <summary>
/// In-memory registry of live agent WSS connections: deviceId -> socket.
/// The server uses it to push commands to a specific device.
/// In multi-instance deployments this should be replaced by a backplane; currently single-instance.
/// </summary>
public sealed class AgentConnectionRegistry
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();

    public IReadOnlyCollection<string> ConnectedDevices => _connections.Keys.ToArray();

    public bool IsConnected(string deviceId) => _connections.ContainsKey(deviceId);

    public void Register(string deviceId, WebSocket socket) => _connections[deviceId] = socket;

    public void Unregister(string deviceId, WebSocket socket)
    {
        // Remove only if this is still the same socket, safe across reconnects.
        if (_connections.TryGetValue(deviceId, out var current) && current == socket)
            _connections.TryRemove(deviceId, out _);
    }

    /// <summary>Sends a signed command to a device. False when the device is offline.</summary>
    public async Task<bool> TrySendAsync(string deviceId, AgentCommand cmd, CancellationToken ct)
    {
        if (!_connections.TryGetValue(deviceId, out var socket) || socket.State != WebSocketState.Open)
            return false;

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(cmd, AgentJsonContext.Default.AgentCommand);
        await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct);
        return true;
    }
}
