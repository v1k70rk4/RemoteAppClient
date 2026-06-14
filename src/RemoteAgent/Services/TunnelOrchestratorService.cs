using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Commands;
using RemoteAgent.Configuration;
using RemoteAgent.Tunnel;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Services;

/// <summary>
/// Opens and closes the reverse tunnel based on already verified commands from the bus.
/// Only one tunnel is active at a time. Idle timeout closes it automatically.
/// </summary>
public sealed class TunnelOrchestratorService(
    IOptions<AgentOptions> options,
    CommandBus bus,
    TunnelState state,
    RemoteAgent.Update.UpdateInstaller updateInstaller,
    AgentUplink uplink,
    ILoggerFactory loggerFactory,
    ILogger<TunnelOrchestratorService> logger) : BackgroundService
{
    private readonly TunnelOptions _opt = options.Value.Tunnel;
    private SshReverseTunnel? _tunnel;
    private DateTimeOffset _lastActivity;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Separate idle watchdog closes inactive tunnels even when no command arrives.
        var watchdog = Task.Run(() => IdleWatchdogAsync(stoppingToken), stoppingToken);

        try
        {
            await foreach (var cmd in bus.ReadAllAsync(stoppingToken))
            {
                try { await HandleAsync(cmd, stoppingToken); }
                catch (Exception ex) { logger.LogError(ex, L.TunnelOrchestratorService_TunnelCommandProcessingFailedType, cmd.Type); }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            await CloseTunnelAsync();
            await watchdog;
        }
    }

    private async Task HandleAsync(AgentCommand cmd, CancellationToken ct)
    {
        switch (cmd.Type)
        {
            case CommandTypes.OpenTunnel:
                await OpenTunnelAsync(cmd, ct);
                break;
            case CommandTypes.CloseTunnel:
                await CloseTunnelAsync();
                break;
            case CommandTypes.Update:
                await updateInstaller.ApplyAsync(
                    cmd.Data?.UpdateTarget, cmd.Data?.UpdateVersion, cmd.Data?.UpdateUrl, cmd.Data?.UpdateSha256, ct);
                break;
            case CommandTypes.Message:
                await MessageCommandAsync(cmd, ct);
                break;
            case CommandTypes.Power:
                await PowerCommandAsync(cmd, ct);
                break;
            default:
                logger.LogWarning(L.TunnelOrchestratorService_UnknownCommandType, cmd.Type);
                break;
        }
    }

    private async Task OpenTunnelAsync(AgentCommand cmd, CancellationToken ct)
    {
        var data = cmd.Data;
        int remotePort = data?.RemotePort ?? 0;

        // Local VNC lock: when the device is locally disabled, do not open a tunnel and log the attempt.
        if (RemoteAgent.Vnc.VncLock.IsLocked())
        {
            RemoteAgent.Vnc.VncLock.Log(L.TunnelOrchestratorService_RemoteAccessTunnelDENIEDThis);
            logger.LogWarning(L.TunnelOrchestratorService_OpenTunnelDeniedThisDevice);
            await uplink.ReportAccessResultAsync(cmd.Nonce, "locked", ct);
            return;
        }

        if (remotePort <= 0)
        {
            logger.LogWarning(L.TunnelOrchestratorService_OpenTunnelWithInvalidRemote, remotePort);
            await uplink.ReportAccessResultAsync(cmd.Nonce, "denied", ct);
            return;
        }

        // Access policy from the server-signed command: consent and unattended access.
        var outcome = await ConsentGateAsync(data?.ConsentRequired ?? false, data?.UnattendedAllowed ?? true, ct);
        await uplink.ReportAccessResultAsync(cmd.Nonce, outcome, ct);
        if (outcome is not ("auto" or "granted"))
            return; // denied / timeout / no user; tunnel stays closed

        // Each open-tunnel can get a fresh server-assigned random port, so close the existing
        // tunnel and open a new one to avoid keeping the old port stuck.
        if (_tunnel is not null)
            await _tunnel.StopAsync();

        _tunnel = new SshReverseTunnel(_opt, loggerFactory.CreateLogger<SshReverseTunnel>());
        await _tunnel.StartAsync(remotePort, ct);
        state.Set(_tunnel.IsRunning);
        _lastActivity = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Access gate before opening the tunnel. Returns the outcome to the server/console too:
    /// "auto" (no consent, allowed) | "granted" | "denied" | "timeout" | "no-user".
    /// - No signed-in user: unattended policy decides.
    /// - Signed-in user + consentRequired: WTS Yes/No prompt, only Yes allows access.
    /// </summary>
    private async Task<string> ConsentGateAsync(bool consentRequired, bool unattendedAllowed, CancellationToken ct)
    {
        bool userPresent = RemoteAgent.Consent.ConsentPrompt.HasActiveUser();

        if (!userPresent)
        {
            if (!unattendedAllowed)
            {
                RemoteAgent.Vnc.VncLock.Log(L.TunnelOrchestratorService_RemoteAccessDENIEDNoSigned);
                logger.LogWarning(L.TunnelOrchestratorService_OpenTunnelDeniedNoActive);
                return "no-user";
            }
            return "auto"; // unattended allowed; proceed without consent
        }

        if (!consentRequired) return "auto";

        // The WTS prompt blocks until answer/timeout; run it in the background to keep async flow free.
        var outcome = await Task.Run(() => RemoteAgent.Consent.ConsentPrompt.Ask(
            L.TunnelOrchestratorService_RemoteAccess,
            L.TunnelOrchestratorService_AnAdministratorWantsToConnect,
            timeoutSeconds: 30), ct);

        switch (outcome)
        {
            case RemoteAgent.Consent.ConsentPrompt.Outcome.Granted:
                RemoteAgent.Vnc.VncLock.Log(L.TunnelOrchestratorService_RemoteAccessALLOWEDByThe);
                return "granted";
            case RemoteAgent.Consent.ConsentPrompt.Outcome.Timeout:
                RemoteAgent.Vnc.VncLock.Log(L.TunnelOrchestratorService_RemoteAccessTheUserDid);
                logger.LogWarning(L.TunnelOrchestratorService_OpenTunnelConsentTimeout);
                return "timeout";
            default:
                RemoteAgent.Vnc.VncLock.Log(L.Format(L.TunnelOrchestratorService_RemoteAccessDENIEDByThe, outcome));
                logger.LogWarning(L.TunnelOrchestratorService_OpenTunnelDeniedConsentOutcome, outcome);
                return "denied";
        }
    }

    /// <summary>
    /// "message" command: shows a WTS prompt to the signed-in user and reports the outcome via uplink.
    /// - availability: Yes/No "may I connect now" (30s). Yes -> "available" (console connects immediately);
    ///   No -> "busy"; timeout -> "no-answer"; nobody -> "no-user".
    /// - text: shows "{from} sent a message" + the body with OK -> "delivered"; nobody -> "no-user".
    /// </summary>
    private async Task MessageCommandAsync(AgentCommand cmd, CancellationToken ct)
    {
        var d = cmd.Data;
        var from = string.IsNullOrWhiteSpace(d?.MessageFrom) ? "?" : d!.MessageFrom!;

        if (d?.MessageKind == "text")
        {
            bool shown = RemoteAgent.Consent.ConsentPrompt.Notify(
                L.Format(L.TunnelOrchestratorService_MessageFromTitle, from), d.MessageText ?? "");
            await uplink.ReportAccessResultAsync(cmd.Nonce, shown ? "delivered" : "no-user", ct);
            return;
        }

        // availability
        if (!RemoteAgent.Consent.ConsentPrompt.HasActiveUser())
        {
            await uplink.ReportAccessResultAsync(cmd.Nonce, "no-user", ct);
            return;
        }

        var outcome = await Task.Run(() => RemoteAgent.Consent.ConsentPrompt.Ask(
            L.TunnelOrchestratorService_RemoteAccess,
            L.Format(L.TunnelOrchestratorService_AvailabilityQuestion, from),
            timeoutSeconds: 30), ct);

        // "Yes" → the console connects right away (no follow-up box). Timeout ("no-answer") is reported
        // separately from an explicit "No" ("busy") so the console can decide what to do when nobody answers.
        var result = outcome switch
        {
            RemoteAgent.Consent.ConsentPrompt.Outcome.Granted => "available",
            RemoteAgent.Consent.ConsentPrompt.Outcome.Timeout => "no-answer",
            _ => "busy", // Denied / Error
        };
        await uplink.ReportAccessResultAsync(cmd.Nonce, result, ct);
    }

    /// <summary>
    /// "power" command: a fixed, server-vetted power action (no shell string on the wire). Reports the
    /// outcome via the uplink so the console gets feedback: "scheduled" | "cancelled" | "logged-out" |
    /// "no-user" | "failed". Restart uses a 60s grace, so the report reaches the console before reboot.
    /// </summary>
    private async Task PowerCommandAsync(AgentCommand cmd, CancellationToken ct)
    {
        var outcome = cmd.Data?.PowerAction switch
        {
            "restart"       => RemoteAgent.Power.PowerControl.Restart(force: false) ? "scheduled" : "failed",
            "force-restart" => RemoteAgent.Power.PowerControl.Restart(force: true)  ? "scheduled" : "failed",
            "cancel"        => Cancelled(),
            "logout"        => RemoteAgent.Power.PowerControl.LogoffActiveUser() ? "logged-out" : "no-user",
            _               => "failed",
        };
        await uplink.ReportAccessResultAsync(cmd.Nonce, outcome, ct);

        static string Cancelled() { RemoteAgent.Power.PowerControl.Cancel(); return "cancelled"; }
    }

    private async Task CloseTunnelAsync()
    {
        if (_tunnel is null) return;
        await _tunnel.StopAsync();
        state.Set(false);
    }

    private async Task IdleWatchdogAsync(CancellationToken ct)
    {
        var idle = TimeSpan.FromSeconds(_opt.IdleTimeoutSeconds);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                if (_tunnel is { IsRunning: true } && DateTimeOffset.UtcNow - _lastActivity > idle)
                {
                    logger.LogInformation(L.TunnelOrchestratorService_TunnelIdleTimeoutIdleS, idle.TotalSeconds);
                    await CloseTunnelAsync();
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }
}
