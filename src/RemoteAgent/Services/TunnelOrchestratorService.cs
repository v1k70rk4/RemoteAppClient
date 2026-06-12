using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Commands;
using RemoteAgent.Configuration;
using RemoteAgent.Tunnel;

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
                catch (Exception ex) { logger.LogError(ex, "Tunnel parancs feldolgozása sikertelen: {Type}", cmd.Type); }
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
                await OpenTunnelAsync(cmd.Data?.RemotePort ?? 0, ct);
                break;
            case CommandTypes.CloseTunnel:
                await CloseTunnelAsync();
                break;
            case CommandTypes.Update:
                await updateInstaller.ApplyAsync(
                    cmd.Data?.UpdateTarget, cmd.Data?.UpdateVersion, cmd.Data?.UpdateUrl, cmd.Data?.UpdateSha256, ct);
                break;
            default:
                logger.LogWarning("Ismeretlen parancs: {Type}", cmd.Type);
                break;
        }
    }

    private async Task OpenTunnelAsync(int remotePort, CancellationToken ct)
    {
        if (remotePort <= 0)
        {
            logger.LogWarning("open-tunnel érvénytelen távoli porttal ({Port}), kihagyva.", remotePort);
            return;
        }

        // Minden open-tunnel friss portra jöhet (a szerver random portot oszt), ezért a
        // meglévő tunnelt lezárjuk és újat nyitunk — különben a régi port "beragadna".
        if (_tunnel is not null)
            await _tunnel.StopAsync();

        _tunnel = new SshReverseTunnel(_opt, loggerFactory.CreateLogger<SshReverseTunnel>());
        await _tunnel.StartAsync(remotePort, ct);
        state.Set(_tunnel.IsRunning);
        _lastActivity = DateTimeOffset.UtcNow;
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
                    logger.LogInformation("Tunnel idle-timeout ({Idle}s), lebontás.", idle.TotalSeconds);
                    await CloseTunnelAsync();
                }
            }
        }
        catch (OperationCanceledException) { /* leállás */ }
    }
}
