using System.IO.Pipes;

namespace RemoteClient;

/// <summary>
/// A HELYI agent brókerével beszél (named pipe): a gép enrollment-kulcsával nyit
/// <c>ssh -L</c> forwardokat a bástyához (admin API / cél-gép VNC). Egy kapcsolat = egy
/// session; a forwardok addig élnek, amíg a kapcsolat nyitva van (záráskor az agent
/// lebontja őket). A kliensnek NINCS saját SSH-kulcsa — a gép identitása a belépő, és csak
/// BELÉPTETETT gépen (ahol fut az agent) működik a konzol.
///
/// BINÁRIS protokoll: a kliens int32 távoli portot ír, az agent int32 helyi portot válaszol
/// (0 = hiba). Nincs szöveg/sorvég/BOM gond.
/// </summary>
public sealed class BrokerClient : IDisposable
{
    public const string PipeName = "RemoteAgent.broker";

    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private BrokerClient(NamedPipeClientStream pipe) => _pipe = pipe;

    /// <summary>Csatlakozás a helyi brókerhez HÁTTÉRSZÁLON (nem fagyasztja a UI-t); null, ha nincs/nem fut az agent.</summary>
    public static Task<BrokerClient?> TryConnectAsync(int timeoutMs = 3000) =>
        Task.Run<BrokerClient?>(() =>
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(timeoutMs); // szinkron, de háttérszálon; az ERROR_PIPE_BUSY-t + timeoutot kezeli
                return new BrokerClient(pipe);
            }
            catch { return null; }
        });

    /// <summary>Forward kérése a bástya egy portjára (admin API 5000 / cél VNC bástya-port). Helyi portot ad.</summary>
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
                throw new InvalidOperationException("A helyi agent nem tudott forwardot nyitni (lásd az agent EventLogját).");
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
