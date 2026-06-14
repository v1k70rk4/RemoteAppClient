using System.Collections.Generic;

namespace RemoteAgent.Updater.Localization;

internal static partial class Strings
{
    private static readonly Dictionary<string, string> Hu = new()
    {
        [nameof(SupervisorWorker_001)] = "agent beragadt (életjel ~{0:F0}s régi) — kényszerített újraindítás",
        [nameof(SupervisorWorker_002)] = "A RemoteAgent nem fut ({State}) — indítás.",
        [nameof(SupervisorWorker_003)] = "agent leállt → újraindítva",
        [nameof(SupervisorWorker_004)] = "agent indítása sikertelen",
        [nameof(SupervisorWorker_005)] = "Túl sok sikertelen helyreállítás ({N}) — parkolás {Min} percre (nincs loop). Reboot után friss próbálkozás.",
        [nameof(SupervisorWorker_006)] = "Üres update.ready (nincs célpath), eldobva.",
        [nameof(SupervisorWorker_007)] = "Update észlelve → {Target} cseréje.",
        [nameof(SupervisorWorker_008)] = "Az exe cseréje nem sikerült (zárolt?). A service-t újraindítom a régivel.",
        [nameof(SupervisorWorker_009)] = "agent frissítve (exe-csere)",
        [nameof(SupervisorWorker_010)] = "Update alkalmazva, a RemoteAgent újraindítva.",
        [nameof(SupervisorWorker_011)] = "A(z) {Service} nem állt le {Sec}s alatt — processz kilövése (PID {Pid}).",
        [nameof(SupervisorWorker_012)] = "Supervisor ciklus hiba.",
        [nameof(SupervisorWorker_013)] = "Kill sikertelen.",
    };
}
