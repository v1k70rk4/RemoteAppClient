using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Commands;
using RemoteAgent.Configuration;
using RemoteAgent.Security;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Services;

/// <summary>
/// Maintains the outbound persistent WSS connection to the server, receives commands,
/// verifies them with <see cref="CommandVerifier"/>, and places valid ones on the bus.
/// Reconnects with exponential backoff after disconnects.
/// </summary>
public sealed class CommandChannelService(
    IOptions<AgentOptions> options,
    CommandVerifier verifier,
    CommandBus bus,
    AgentStatusState status,
    AgentUplink uplink,
    ILogger<CommandChannelService> logger) : BackgroundService
{
    private readonly CommandChannelOptions _opt = options.Value.CommandChannel;
    private readonly string _pfxPath = options.Value.ClientCertPfxPath;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_opt.Url))
        {
            logger.LogWarning(L.CommandChannelService_001);
            return;
        }

        var delay = TimeSpan.FromSeconds(_opt.ReconnectBaseDelaySeconds);
        var maxDelay = TimeSpan.FromSeconds(_opt.ReconnectMaxDelaySeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndListenAsync(stoppingToken);
                delay = TimeSpan.FromSeconds(_opt.ReconnectBaseDelaySeconds); // success resets backoff
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, L.CommandChannelService_002, delay.TotalSeconds);
            }
            finally { status.SetC2Connected(false); } // disconnected; status pipe reflects this

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));
        }
    }

    private async Task ConnectAndListenAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();

        if (!string.IsNullOrWhiteSpace(_pfxPath) || !string.IsNullOrWhiteSpace(_opt.ClientCertThumbprint))
            ws.Options.ClientCertificates.Add(
                CertHelper.ResolveClientCertificate(_pfxPath, _opt.ClientCertThumbprint));

        if (!string.IsNullOrWhiteSpace(_opt.ServerCertPinSha256))
            ws.Options.RemoteCertificateValidationCallback =
                CertHelper.PinnedServerValidator(_opt.ServerCertPinSha256);

        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(_opt.KeepAliveIntervalSeconds);
        // Pong timeout: without this, ReceiveAsync can miss a post-sleep half-open connection
        // and block until the OS TCP timeout, often hours. This detects dead connections after
        // roughly interval+timeout, throws, and the backoff loop reconnects immediately.
        ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(_opt.KeepAliveTimeoutSeconds);

        logger.LogInformation(L.CommandChannelService_003, _opt.Url);
        await ws.ConnectAsync(new Uri(_opt.Url), ct);
        logger.LogInformation(L.CommandChannelService_004);
        status.SetC2Connected(true); // the status pipe reports this to the client
        uplink.SetSocket(ws);        // from here we can send result messages back

        var buffer = new byte[8192];
        var message = new MemoryStream();

        try
        {
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                    return;
                }
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            await HandleMessageAsync(message.ToArray(), ct);
        }
        }
        finally { uplink.Clear(ws); }
    }

    private async Task HandleMessageAsync(byte[] raw, CancellationToken ct)
    {
        AgentCommand? cmd;
        try
        {
            cmd = JsonSerializer.Deserialize(raw, AgentJsonContext.Default.AgentCommand);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, L.CommandChannelService_005);
            return;
        }

        if (cmd is null || !verifier.Verify(cmd))
            return;

        if (cmd.Type == CommandTypes.Ping)
        {
            logger.LogDebug(L.CommandChannelService_006);
            return;
        }

        logger.LogInformation(L.CommandChannelService_007, cmd.Type);
        await bus.PublishAsync(cmd, ct);
    }
}
