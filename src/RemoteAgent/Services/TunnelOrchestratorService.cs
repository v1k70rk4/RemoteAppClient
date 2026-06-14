using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Commands;
using RemoteAgent.Configuration;
using RemoteAgent.Tunnel;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Services;

/// <summary>
/// A busról érkező, MÁR ELLENŐRZÖTT parancsok alapján nyitja/zárja a reverse tunnelt.
/// Egyszerre egy tunnel él. Idle-timeout után magától lebont.
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
        // Külön idle-watchdog, hogy a tétlen tunnelt akkor is lezárjuk, ha nem jön parancs.
        var watchdog = Task.Run(() => IdleWatchdogAsync(stoppingToken), stoppingToken);

        try
        {
            await foreach (var cmd in bus.ReadAllAsync(stoppingToken))
            {
                try { await HandleAsync(cmd, stoppingToken); }
                catch (Exception ex) { logger.LogError(ex, L.TunnelOrchestratorService_001, cmd.Type); }
            }
        }
        catch (OperationCanceledException) { /* leállás */ }
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
            default:
                logger.LogWarning(L.TunnelOrchestratorService_015, cmd.Type);
                break;
        }
    }

    private async Task OpenTunnelAsync(AgentCommand cmd, CancellationToken ct)
    {
        var data = cmd.Data;
        int remotePort = data?.RemotePort ?? 0;

        // HELYI VNC-zár: ha a gépet helyileg letiltották, NEM nyitunk tunnelt, és naplózzuk a próbálkozást.
        if (RemoteAgent.Vnc.VncLock.IsLocked())
        {
            RemoteAgent.Vnc.VncLock.Log(L.TunnelOrchestratorService_002);
            logger.LogWarning(L.TunnelOrchestratorService_003);
            await uplink.ReportAccessResultAsync(cmd.Nonce, "locked", ct);
            return;
        }

        if (remotePort <= 0)
        {
            logger.LogWarning(L.TunnelOrchestratorService_004, remotePort);
            await uplink.ReportAccessResultAsync(cmd.Nonce, "denied", ct);
            return;
        }

        // Hozzáférés-policy (a szerver aláírt parancsából): hozzájárulás + felügyelet nélküli hozzáférés.
        var outcome = await ConsentGateAsync(data?.ConsentRequired ?? false, data?.UnattendedAllowed ?? true, ct);
        await uplink.ReportAccessResultAsync(cmd.Nonce, outcome, ct);
        if (outcome is not ("auto" or "granted"))
            return; // elutasítva / timeout / nincs user — a tunnel nem nyílik

        // Minden open-tunnel friss portra jöhet (a szerver random portot oszt), ezért a
        // meglévő tunnelt lezárjuk és újat nyitunk — különben a régi port "beragadna".
        if (_tunnel is not null)
            await _tunnel.StopAsync();

        _tunnel = new SshReverseTunnel(_opt, loggerFactory.CreateLogger<SshReverseTunnel>());
        await _tunnel.StartAsync(remotePort, ct);
        state.Set(_tunnel.IsRunning);
        _lastActivity = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Hozzáférés-kapu a tunnel-nyitás előtt. Visszaadja a kimenetelt a szervernek/konzolnak is:
    /// "auto" (nincs consent, engedett) | "granted" | "denied" | "timeout" | "no-user".
    /// - Nincs bejelentkezett felhasználó → az unattended-policy dönt.
    /// - Van felhasználó + consentRequired → WTS Igen/Nem prompt; csak „Igen" enged.
    /// </summary>
    private async Task<string> ConsentGateAsync(bool consentRequired, bool unattendedAllowed, CancellationToken ct)
    {
        bool userPresent = RemoteAgent.Consent.ConsentPrompt.HasActiveUser();

        if (!userPresent)
        {
            if (!unattendedAllowed)
            {
                RemoteAgent.Vnc.VncLock.Log(L.TunnelOrchestratorService_005);
                logger.LogWarning(L.TunnelOrchestratorService_006);
                return "no-user";
            }
            return "auto"; // unattended engedett → mehet consent nélkül
        }

        if (!consentRequired) return "auto";

        // A WTS prompt blokkol a válaszig/timeoutig — háttérszálon, hogy ne fogja meg az async folyamot.
        var outcome = await Task.Run(() => RemoteAgent.Consent.ConsentPrompt.Ask(
            L.TunnelOrchestratorService_007,
            L.TunnelOrchestratorService_008,
            timeoutSeconds: 30), ct);

        switch (outcome)
        {
            case RemoteAgent.Consent.ConsentPrompt.Outcome.Granted:
                RemoteAgent.Vnc.VncLock.Log(L.TunnelOrchestratorService_009);
                return "granted";
            case RemoteAgent.Consent.ConsentPrompt.Outcome.Timeout:
                RemoteAgent.Vnc.VncLock.Log(L.TunnelOrchestratorService_010);
                logger.LogWarning(L.TunnelOrchestratorService_011);
                return "timeout";
            default:
                RemoteAgent.Vnc.VncLock.Log(L.Format(L.TunnelOrchestratorService_012, outcome));
                logger.LogWarning(L.TunnelOrchestratorService_013, outcome);
                return "denied";
        }
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
                    logger.LogInformation(L.TunnelOrchestratorService_014, idle.TotalSeconds);
                    await CloseTunnelAsync();
                }
            }
        }
        catch (OperationCanceledException) { /* leállás */ }
    }
}
