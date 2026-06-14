using System.Collections.Generic;

namespace RemoteAgent.Updater.Localization;

internal static partial class Strings
{
    private static readonly Dictionary<string, string> Hu = new()
    {
        [nameof(SupervisorWorker_AgentHungHeartbeatAbout0)] = "agent beragadt (életjel ~{0:F0}s régi) — kényszerített újraindítás",
        [nameof(SupervisorWorker_RemoteAgentIsNotRunningState)] = "A RemoteAgent nem fut ({State}) — indítás.",
        [nameof(SupervisorWorker_AgentStoppedRestarted)] = "agent leállt → újraindítva",
        [nameof(SupervisorWorker_AgentStartFailed)] = "agent indítása sikertelen",
        [nameof(SupervisorWorker_TooManyFailedRecoveryAttempts)] = "Túl sok sikertelen helyreállítás ({N}) — parkolás {Min} percre (nincs loop). Reboot után friss próbálkozás.",
        [nameof(SupervisorWorker_EmptyUpdateReadyNoTarget)] = "Üres update.ready (nincs célpath), eldobva.",
        [nameof(SupervisorWorker_UpdateDetectedReplacingTarget)] = "Update észlelve → {Target} cseréje.",
        [nameof(SupervisorWorker_CouldNotReplaceTheExe)] = "Az exe cseréje nem sikerült (zárolt?). A service-t újraindítom a régivel.",
        [nameof(SupervisorWorker_AgentUpdatedExeReplacement)] = "agent frissítve (exe-csere)",
        [nameof(SupervisorWorker_UpdateAppliedRemoteAgentRestarted)] = "Update alkalmazva, a RemoteAgent újraindítva.",
        [nameof(SupervisorWorker_ServiceDidNotStopWithin)] = "A(z) {Service} nem állt le {Sec}s alatt — processz kilövése (PID {Pid}).",
        [nameof(SupervisorWorker_SupervisorCycleError)] = "Supervisor ciklus hiba.",
        [nameof(SupervisorWorker_KillFailed)] = "Kill sikertelen.",
    };
}
