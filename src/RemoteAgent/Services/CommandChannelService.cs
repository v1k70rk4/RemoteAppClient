using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Commands;
using RemoteAgent.Configuration;
using RemoteAgent.Security;

namespace RemoteAgent.Services;

/// <summary>
/// Kimenő, perzisztens WSS kapcsolatot tart a szerver felé, fogadja a parancsokat,
/// ellenőrzi őket (<see cref="CommandVerifier"/>), és a busra teszi a jókat.
/// Lekapcsolódásnál exponenciális backoff-fal újracsatlakozik.
/// </summary>
public sealed class CommandChannelService(
    IOptions<AgentOptions> options,
    CommandVerifier verifier,
    CommandBus bus,
    ILogger<CommandChannelService> logger) : BackgroundService
{
    private readonly CommandChannelOptions _opt = options.Value.CommandChannel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_opt.Url))
        {
            logger.LogWarning("Nincs parancscsatorna URL konfigurálva, a szolgáltatás tétlen.");
            return;
        }

        var delay = TimeSpan.FromSeconds(_opt.ReconnectBaseDelaySeconds);
        var maxDelay = TimeSpan.FromSeconds(_opt.ReconnectMaxDelaySeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndListenAsync(stoppingToken);
                delay = TimeSpan.FromSeconds(_opt.ReconnectBaseDelaySeconds); // siker → reset
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Parancscsatorna hiba, újracsatlakozás {Delay}s múlva.", delay.TotalSeconds);
            }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));
        }
    }

    private async Task ConnectAndListenAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();

        if (!string.IsNullOrWhiteSpace(_opt.ClientCertThumbprint))
            ws.Options.ClientCertificates.Add(CertHelper.LoadClientCertificate(_opt.ClientCertThumbprint));

        if (!string.IsNullOrWhiteSpace(_opt.ServerCertPinSha256))
            ws.Options.RemoteCertificateValidationCallback =
                CertHelper.PinnedServerValidator(_opt.ServerCertPinSha256);

        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        logger.LogInformation("Csatlakozás a parancscsatornához: {Url}", _opt.Url);
        await ws.ConnectAsync(new Uri(_opt.Url), ct);
        logger.LogInformation("Parancscsatorna él.");

        var buffer = new byte[8192];
        var message = new MemoryStream();

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

    private async Task HandleMessageAsync(byte[] raw, CancellationToken ct)
    {
        AgentCommand? cmd;
        try
        {
            cmd = JsonSerializer.Deserialize(raw, AgentJsonContext.Default.AgentCommand);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Értelmezhetetlen parancs üzenet, eldobva.");
            return;
        }

        if (cmd is null || !verifier.Verify(cmd))
            return;

        if (cmd.Type == CommandTypes.Ping)
        {
            logger.LogDebug("Ping fogadva.");
            return;
        }

        logger.LogInformation("Hiteles parancs fogadva: {Type}", cmd.Type);
        await bus.PublishAsync(cmd, ct);
    }
}
