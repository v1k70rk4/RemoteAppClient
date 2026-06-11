using System.Threading.Channels;

namespace RemoteAgent.Commands;

/// <summary>
/// Egyirányú, korlátos csatorna a parancscsatorna (producer) és a
/// tunnel-orchestrátor (consumer) közt. Csak MÁR ELLENŐRZÖTT parancs kerül ide.
/// </summary>
public sealed class CommandBus
{
    private readonly Channel<AgentCommand> _channel =
        Channel.CreateBounded<AgentCommand>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public ValueTask PublishAsync(AgentCommand cmd, CancellationToken ct) =>
        _channel.Writer.WriteAsync(cmd, ct);

    public IAsyncEnumerable<AgentCommand> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
