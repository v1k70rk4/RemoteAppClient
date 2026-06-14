using System.IO.Pipes;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient;

/// <summary>
/// Talks to the local agent broker over a named pipe. The agent opens <c>ssh -L</c>
/// forwards to the bastion using the device enrollment key (admin API / target-device VNC).
/// One connection equals one session; forwards live while the connection is open and are
/// torn down by the agent on close. The client has no SSH key of its own; device identity
/// is the credential, and the console only works on enrolled devices with a running agent.
///
/// Binary protocol: client writes int32 remote port, agent answers int32 local port
/// (0 = error). No text, line ending, or BOM ambiguity.
/// </summary>
public sealed class BrokerClient : IDisposable
{
    public const string PipeName = "RemoteAgent.broker";

    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private BrokerClient(NamedPipeClientStream pipe) => _pipe = pipe;

    /// <summary>Connects to the local broker on a background thread so the UI does not freeze; null if missing/not running.</summary>
    public static Task<BrokerClient?> TryConnectAsync(int timeoutMs = 3000) =>
        Task.Run<BrokerClient?>(() =>
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(timeoutMs); // synchronous but on background thread; handles ERROR_PIPE_BUSY and timeout
                return new BrokerClient(pipe);
            }
            catch { return null; }
        });

    /// <summary>Requests a forward to a bastion port (admin API 5000 / target VNC bastion port). Returns local port.</summary>
    public async Task<int> ForwardAsync(int remotePort, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await _pipe.WriteAsync(BitConverter.GetBytes(remotePort), ct);
            await _pipe.FlushAsync(ct);

            var buf = new byte[4];
            await _pipe.ReadExactlyAsync(buf, ct);
            int localPort = BitConverter.ToInt32(buf, 0);
            if (localPort <= 0)
                throw new InvalidOperationException(L.BrokerClient_001);
            return localPort;
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        try { _gate.Dispose(); } catch { /* best effort */ }
        try { _pipe.Dispose(); } catch { /* best effort */ }
    }
}
