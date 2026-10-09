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
    private readonly ConcurrentDictionary<string, Connection> _connections = new();

    /// <summary>A live socket with its send gate: a WebSocket allows one outstanding send at a time, and two
    /// operators acting on the same device at once (or a command racing a close frame) used to throw.</summary>
    private sealed record Connection(WebSocket Socket)
    {
        public SemaphoreSlim SendGate { get; } = new(1, 1);
    }

    private static readonly TimeSpan SendWait = TimeSpan.FromSeconds(10);

    /// <summary>Runs one send on the socket while holding its gate. False when the gate could not be taken in
    /// time (a peer that stopped reading): the caller treats that like an offline device.</summary>
    private static async Task<bool> SendLockedAsync(Connection c, Func<WebSocket, Task> send, CancellationToken ct)
    {
        if (!await c.SendGate.WaitAsync(SendWait, ct)) return false;
        try { await send(c.Socket); return true; }
        finally { c.SendGate.Release(); }
    }

    /// <summary>Per-device rolling C2 (re)connect history, in-memory only. Frequent reconnects = flaky link:
    /// the agent is likely alive but on a poor network, as opposed to a genuinely offline/dead device.</summary>
    private readonly ConcurrentDictionary<string, ReconnectWindow> _reconnects = new();
    private static readonly TimeSpan ReconnectWindowSpan = TimeSpan.FromHours(1);

    public IReadOnlyCollection<string> ConnectedDevices => _connections.Keys.ToArray();

    public bool IsConnected(string deviceId) => _connections.ContainsKey(deviceId);

    // ---- shutdown -------------------------------------------------------------------------------------
    // Without this a server stop took the host's full 30 s shutdown timeout every time: the agent sockets
    // are long-lived requests that would happily stay open, and Kestrel waits for in-flight requests. So on
    // stopping we tell every agent to leave (close frame 1001 "server restarting" - they reconnect with
    // their normal backoff, no agent change needed), and a few seconds later abort whoever did not answer.

    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Fires a grace period into shutdown; the socket handlers link their loops to it.</summary>
    public CancellationToken ShutdownToken => _shutdown.Token;

    /// <summary>Sends the close frame to every live socket, then arms <see cref="ShutdownToken"/> to fire after
    /// <paramref name="grace"/>. Returns how many sockets were told.</summary>
    public async Task<int> BeginShutdownAsync(TimeSpan grace)
    {
        var sockets = _connections.Values.ToArray();
        using var cts = new CancellationTokenSource(grace);
        await Task.WhenAll(sockets.Select(async c =>
        {
            try
            {
                // Output only: the handler's pending ReceiveAsync picks up the agent's reply and ends the loop.
                await SendLockedAsync(c, async s =>
                {
                    if (s.State == WebSocketState.Open)
                        await s.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "server restarting", cts.Token);
                }, cts.Token);
            }
            catch { /* a socket that is already gone is exactly what we want */ }
        }));
        _shutdown.CancelAfter(grace);
        return sockets.Length;
    }

    public void Register(string deviceId, WebSocket socket)
    {
        _connections[deviceId] = new Connection(socket);
        _reconnects.GetOrAdd(deviceId, static _ => new ReconnectWindow()).Mark(); // track C2 churn for the flaky-link signal
    }

    public void Unregister(string deviceId, WebSocket socket)
    {
        // Remove only if this is still the same socket, safe across reconnects.
        if (_connections.TryGetValue(deviceId, out var current) && current.Socket == socket)
            _connections.TryRemove(new KeyValuePair<string, Connection>(deviceId, current));
    }

    /// <summary>
    /// Ends a device's live connection, for a device that was just deleted: it is told to leave (close frame
    /// 1008 "device removed") and the handler's loop ends with it. Reconnecting is then refused, since the
    /// device no longer exists. A no-op when it is not connected.
    /// </summary>
    public async Task DisconnectAsync(string deviceId)
    {
        _reconnects.TryRemove(deviceId, out _);
        if (!_connections.TryRemove(deviceId, out var c)) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await SendLockedAsync(c, async s =>
            {
                if (s.State == WebSocketState.Open)
                    await s.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "device removed", cts.Token);
            }, cts.Token);
        }
        catch { /* gone already */ }
        try { c.Socket.Abort(); } catch { }
    }

    /// <summary>Sends a signed command to a device. False when the device is offline, or when its socket stayed
    /// busy with another send for too long.</summary>
    public async Task<bool> TrySendAsync(string deviceId, AgentCommand cmd, CancellationToken ct)
    {
        if (!_connections.TryGetValue(deviceId, out var c) || c.Socket.State != WebSocketState.Open)
            return false;

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(cmd, AgentJsonContext.Default.AgentCommand);
        return await SendLockedAsync(c, async s =>
        {
            if (s.State != WebSocketState.Open) throw new WebSocketException(WebSocketError.InvalidState);
            await s.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct);
        }, ct);
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
