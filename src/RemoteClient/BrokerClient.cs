using System.IO.Pipes;
using System.Text;

namespace RemoteClient;

/// <summary>
/// A HELYI agent brókerével beszél (named pipe): a gép enrollment-kulcsával nyit
/// <c>ssh -L</c> forwardokat a bástyához (admin API / cél-gép VNC). Egy kapcsolat = egy
/// session; a forwardok addig élnek, amíg a kapcsolat nyitva van (záráskor az agent
/// lebontja őket). A kliensnek NINCS saját SSH-kulcsa — a gép identitása a belépő, és csak
/// BELÉPTETETT gépen (ahol fut az agent) működik a konzol.
/// </summary>
public sealed class BrokerClient : IDisposable
{
    public const string PipeName = "RemoteAgent.broker";

    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private BrokerClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        _writer = new StreamWriter(pipe, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };
    }

    /// <summary>Csatlakozás a helyi brókerhez (NEM blokkolja a UI-t); null, ha nincs/nem fut az agent.</summary>
    public static async Task<BrokerClient?> TryConnectAsync(int timeoutMs = 3000)
    {
        try
        {
            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeoutMs);
            return new BrokerClient(pipe);
        }
        catch { return null; }
    }

    /// <summary>Forward kérése a bástya egy portjára (admin API 5000 / cél VNC bástya-port). Helyi portot ad.</summary>
    public async Task<int> ForwardAsync(int remotePort, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await _writer.WriteLineAsync($"FORWARD {remotePort}".AsMemory(), ct);
            var resp = await _reader.ReadLineAsync(ct);
            if (resp is not null && resp.StartsWith("OK ", StringComparison.Ordinal) && int.TryParse(resp.AsSpan(3), out var port))
                return port;
            throw new InvalidOperationException("A helyi agent nem tudott forwardot nyitni: " + (resp ?? "nincs válasz"));
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        try { _gate.Dispose(); } catch { /* best effort */ }
        try { _pipe.Dispose(); } catch { /* best effort */ }
    }
}
