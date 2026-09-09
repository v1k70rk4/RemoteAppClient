using System.Diagnostics;
using Microsoft.Extensions.Options;
using RemoteAgent.Configuration;
using RemoteAgent.Time;

namespace RemoteAgent.Services;

/// <summary>
/// Keeps the clock disciplined: once shortly after start, then periodically, and immediately whenever
/// something (in practice the command verifier) reports that our time looks wrong.
/// <para>
/// A clock that drifts past the 60s command window takes the device out of reach completely — commands are
/// refused on arrival while telemetry keeps flowing, so the console shows a healthy machine that ignores
/// everything. Fixing that from the server is impossible by definition, so the agent has to fix itself.
/// </para>
/// </summary>
public sealed class TimeSyncService(
    IOptions<AgentOptions> options,
    TimeSyncTrigger trigger,
    ILogger<TimeSyncService> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromHours(6);

    /// <summary>Floor between two w32tm runs. Short on purpose: reacting fast is the whole point, and a
    /// resync is cheap. It only exists so a storm of refused commands cannot become a storm of processes.</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);

    // MONOTONIC, deliberately: this throttle guards the routine that fixes the wall clock, so measuring it
    // with the wall clock is backwards - a backwards jump would shrink the gap and block us exactly when a
    // sync is most needed. Stopwatch keeps ticking straight through a clock change.
    private long _lastStamp = long.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the network settle first: a resync into a dead link just fails and wastes the attempt.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }

        var pending = true;                       // sync once shortly after start
        while (!stoppingToken.IsCancellationRequested)
        {
            if (pending && Due()) { SyncOnce(); pending = false; }

            // While a sync is still owed, come back at the throttle instead of sleeping until the periodic
            // sweep: a request that arrives inside the throttle window must be DEFERRED, never dropped.
            // (It used to be dropped, which is how a device could ask for a fix and then sit broken for
            // six hours while the server watched it refuse every command.)
            try { await trigger.WaitAsync(pending ? MinInterval : Period, stoppingToken); }
            catch (OperationCanceledException) { return; }
            pending = true;
        }
    }

    /// <summary>True when enough monotonic time has passed since the last w32tm run.</summary>
    private bool Due() =>
        _lastStamp == long.MinValue || Stopwatch.GetElapsedTime(_lastStamp) >= MinInterval;

    private void SyncOnce()
    {
        _lastStamp = Stopwatch.GetTimestamp();

        try
        {
            var result = TimeSync.Ensure(options.Value.TimeSync.PeerList);
            // Warning, not Information: the agent's EventLog sink only records Warning and above, and a
            // clock that just moved is precisely what someone reading that log later needs to find.
            if (result.Error is not null) logger.LogWarning("Idoszinkron sikertelen: {Error}", result.Error);
            else if (result.Corrected || result.ServiceStarted || result.SourceConfigured)
                logger.LogWarning("Idoszinkron: {Result}", result.ToString());
            else logger.LogInformation("Idoszinkron (nem kellett igazitani): {Result}", result.ToString());
        }
        catch (Exception ex)
        {
            try { logger.LogError(ex, "Idoszinkron kivetel."); } catch { }
        }
    }
}
