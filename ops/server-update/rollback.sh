#!/usr/bin/env bash
# RemoteServer rollback helper (root, via remoteserver-rollback.service). Restores the binaries and
# database from the most recent backup recorded in last_backup, then restarts and health-checks.
set -uo pipefail
umask 077   # keep any root-created files (logs/temp) non-world-readable

UPD=/var/lib/remoteserver/updates
BK=/var/lib/remoteserver/backups
OPT=/opt/remoteserver
SVC=remoteserver
LOG="$UPD/result.log"

log(){ echo "[$(date +%H:%M:%S)] $*" >> "$LOG"; }
finish(){ echo "$1" > "$UPD/result.status"; date -Iseconds > "$UPD/result.at"; rm -f "$UPD/rollback.trigger"; chmod 644 "$UPD/result.status" "$UPD/result.at" "$LOG" 2>/dev/null || true; exit 0; }

: > "$LOG"
TS=$(cat "$UPD/last_backup" 2>/dev/null || true)
if [ -z "${TS:-}" ] || [ ! -d "$BK/opt-$TS" ]; then log "No backup to roll back to."; finish failed; fi

log "Rolling back to $TS"
systemctl stop "$SVC"
rsync -a --delete "$BK/opt-$TS"/ "$OPT"/ 2>>"$LOG"
chown -R remotesrv:remotesrv "$OPT"
[ -f "$BK/db-$TS.sql.gz" ] && gunzip -c "$BK/db-$TS.sql.gz" | mysql remoteserver 2>>"$LOG"
systemctl start "$SVC"

ok=0
for _ in $(seq 1 30); do
  sleep 2
  if systemctl is-active --quiet "$SVC" && curl -fsS -o /dev/null --max-time 3 http://127.0.0.1:5000/health; then ok=1; break; fi
done
if [ "$ok" = 1 ]; then log "Rollback complete."; finish ok; else log "Rollback health check failed."; finish failed; fi
