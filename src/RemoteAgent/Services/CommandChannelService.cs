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

    private bool _noUrlLogged;

    /// <summary>
    /// Outer guard around the reconnect loop. The host ignores background-service failures so one faulty
    /// service cannot take the whole agent down; the flip side is that if this method ever returns, the
    /// agent keeps running - telemetry included - with no control channel at all. That device looks alive
    /// in the console yet can never be commanded, and nothing short of a service restart brings it back.
    /// Even the logging can cause it: an EventLog write throwing inside the catch below (its message limit
    /// is ~32 KB, and stack traces get long) would escape the reconnect loop entirely. So nothing here may
    /// fall through silently - anything unexpected is logged and the loop is restarted.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunChannelLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                SafeLog(ex);
            }

            if (stoppingToken.IsCancellationRequested) return;

            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Logging must never be the thing that kills the channel, so failures to log are swallowed.</summary>
    private void SafeLog(Exception ex)
    {
        try { logger.LogError(ex, "Command channel loop exited unexpectedly; restarting it in 60s."); }
        catch { /* the channel matters more than the log entry */ }
    }

    private async Task RunChannelLoopAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_opt.Url))
        {
            // Logged once: the outer guard retries, and a warning a minute would bury everything else.
            if (!_noUrlLogged) { _noUrlLogged = true; logger.LogWarning(L.CommandChannelService_NoCommandChannelURLConfigured); }
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
                // Guarded: a throwing log write here used to escape the loop and leave the agent deaf.
                try { logger.LogWarning(ex, L.CommandChannelService_CommandChannelErrorReconnectingIn, delay.TotalSeconds); }
                catch { /* keep reconnecting regardless */ }
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

        logger.LogInformation(L.CommandChannelService_ConnectingToCommandChannelUrl, _opt.Url);
        await ws.ConnectAsync(new Uri(_opt.Url), ct);
        logger.LogInformation(L.CommandChannelService_CommandChannelIsLive);
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
            logger.LogWarning(ex, L.CommandChannelService_UnparseableCommandMessageDiscarded);
            return;
        }

        if (cmd is null || !verifier.Verify(cmd))
            return;

        if (cmd.Type == CommandTypes.Ping)
        {
            logger.LogDebug(L.CommandChannelService_PingReceived);
            return;
        }

        logger.LogInformation(L.CommandChannelService_AuthenticatedCommandReceivedType, cmd.Type);
        await bus.PublishAsync(cmd, ct);
    }
}
