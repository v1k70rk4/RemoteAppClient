using System.Threading.Channels;

namespace RemoteAgent.Commands;

/// <summary>
/// One-way bounded channel between the command channel (producer) and tunnel orchestrator
/// (consumer). Only already verified commands are placed here.
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
