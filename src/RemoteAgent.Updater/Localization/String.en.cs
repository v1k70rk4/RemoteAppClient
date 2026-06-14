using System.Collections.Generic;

namespace RemoteAgent.Updater.Localization;

internal static partial class Strings
{
    private static readonly Dictionary<string, string> En = new()
    {
        [nameof(SupervisorWorker_AgentHungHeartbeatAbout0)] = "agent hung (heartbeat about {0:F0}s old) - forced restart",
        [nameof(SupervisorWorker_RemoteAgentIsNotRunningState)] = "RemoteAgent is not running ({State}) - starting.",
        [nameof(SupervisorWorker_AgentStoppedRestarted)] = "agent stopped -> restarted",
        [nameof(SupervisorWorker_AgentStartFailed)] = "agent start failed",
        [nameof(SupervisorWorker_TooManyFailedRecoveryAttempts)] = "Too many failed recovery attempts ({N}); parking for {Min} minutes (no loop). Fresh attempt after reboot.",
        [nameof(SupervisorWorker_EmptyUpdateReadyNoTarget)] = "Empty update.ready (no target path), discarded.",
        [nameof(SupervisorWorker_UpdateDetectedReplacingTarget)] = "Update detected -> replacing {Target}.",
        [nameof(SupervisorWorker_CouldNotReplaceTheExe)] = "Could not replace the exe (locked?). Restarting the service with the old one.",
        [nameof(SupervisorWorker_AgentUpdatedExeReplacement)] = "agent updated (exe replacement)",
        [nameof(SupervisorWorker_UpdateAppliedRemoteAgentRestarted)] = "Update applied, RemoteAgent restarted.",
        [nameof(SupervisorWorker_ServiceDidNotStopWithin)] = "{Service} did not stop within {Sec}s; killing process (PID {Pid}).",
        [nameof(SupervisorWorker_SupervisorCycleError)] = "Supervisor cycle error.",
        [nameof(SupervisorWorker_KillFailed)] = "Kill failed.",
    };
}
