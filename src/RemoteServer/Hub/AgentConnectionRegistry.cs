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

    /// <summary>Per-device rolling C2 (re)connect history, in-memory only. Frequent reconnects = flaky link:
    /// the agent is likely alive but on a poor network, as opposed to a genuinely offline/dead device.</summary>
    private readonly ConcurrentDictionary<string, ReconnectWindow> _reconnects = new();
    private static readonly TimeSpan ReconnectWindowSpan = TimeSpan.FromHours(1);

    public IReadOnlyCollection<string> ConnectedDevices => _connections.Keys.ToArray();

    public bool IsConnected(string deviceId) => _connections.ContainsKey(deviceId);

    public void Register(string deviceId, WebSocket socket)
    {
        _connections[deviceId] = socket;
        _reconnects.GetOrAdd(deviceId, static _ => new ReconnectWindow()).Mark(); // track C2 churn for the flaky-link signal
    }

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

    /// <summary>How many times this device's C2 connection (re)established within the last hour. 0–1 is a stable
    /// link; higher means the agent keeps dropping and reconnecting (flaky network), not a dead device.</summary>
    public int RecentReconnects(string deviceId) =>
        _reconnects.TryGetValue(deviceId, out var w) ? w.CountWithin(ReconnectWindowSpan) : 0;

    /// <summary>Small thread-safe rolling window of recent connect timestamps for one device.</summary>
    private sealed class ReconnectWindow
    {
        private const int Cap = 64; // bound memory for a pathologically flapping device
        private readonly object _gate = new();
        private readonly Queue<DateTimeOffset> _hits = new();

        public void Mark()
        {
            lock (_gate)
            {
                _hits.Enqueue(DateTimeOffset.UtcNow);
                while (_hits.Count > Cap) _hits.Dequeue();
            }
        }

        public int CountWithin(TimeSpan window)
        {
            var cutoff = DateTimeOffset.UtcNow - window;
            lock (_gate)
            {
                while (_hits.Count > 0 && _hits.Peek() < cutoff) _hits.Dequeue();
                return _hits.Count;
            }
        }
    }
}
