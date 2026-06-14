using System.Collections.Generic;

namespace RemoteAgent.Updater.Localization;

internal static partial class Strings
{
    private static readonly Dictionary<string, string> En = new()
    {
        [nameof(SupervisorWorker_001)] = "agent hung (heartbeat about {0:F0}s old) - forced restart",
        [nameof(SupervisorWorker_002)] = "RemoteAgent is not running ({State}) - starting.",
        [nameof(SupervisorWorker_003)] = "agent stopped -> restarted",
        [nameof(SupervisorWorker_004)] = "agent start failed",
        [nameof(SupervisorWorker_005)] = "Too many failed recovery attempts ({N}); parking for {Min} minutes (no loop). Fresh attempt after reboot.",
        [nameof(SupervisorWorker_006)] = "Empty update.ready (no target path), discarded.",
        [nameof(SupervisorWorker_007)] = "Update detected -> replacing {Target}.",
        [nameof(SupervisorWorker_008)] = "Could not replace the exe (locked?). Restarting the service with the old one.",
        [nameof(SupervisorWorker_009)] = "agent updated (exe replacement)",
        [nameof(SupervisorWorker_010)] = "Update applied, RemoteAgent restarted.",
        [nameof(SupervisorWorker_011)] = "{Service} did not stop within {Sec}s; killing process (PID {Pid}).",
        [nameof(SupervisorWorker_012)] = "Supervisor cycle error.",
        [nameof(SupervisorWorker_013)] = "Kill failed.",
    };
}
