# Step 10 - console-driven server self-update (backup -> swap -> migrate -> restart -> auto-rollback).
# systemd .path units watch for trigger files the server drops; root oneshot services run the helpers.
# Mirrors the prod box so the console "Server update" works here too.
# NOTE: DB backup/restore uses LOCAL MariaDB root-socket auth (the default install). With an EXTERNAL
# DB the binary swap still works, but DB dump/migrate is skipped - handle the DB out of band.
require_sudo
sudo mkdir -p /opt/remoteserver-update /var/lib/remoteserver/updates/incoming /var/lib/remoteserver/backups
sudo chown -R "$RAC_SVC_USER:$RAC_SVC_USER" /var/lib/remoteserver/updates
sudo chmod 700 /var/lib/remoteserver/backups

sudo tee /opt/remoteserver-update/deploy.sh >/dev/null <<'DEPLOY'
#!/usr/bin/env bash
# RemoteServer self-update helper (runs as root via remoteserver-update.service, triggered by
# remoteserver-update.path when the server drops apply.trigger). Runs in its own cgroup, so stopping
# remoteserver does not kill it. Flow: backup -> stop -> optional schema upgrade -> swap -> start ->
# health-check -> auto-rollback on failure. MariaDB is reached via root socket auth (no app password).
set -uo pipefail
umask 077   # backups + DB dumps must be root-only (they contain sensitive data)

UPD=/var/lib/remoteserver/updates
INC="$UPD/incoming"
BK=/var/lib/remoteserver/backups
OPT=/opt/remoteserver
SVC=remoteserver
TS=$(date +%Y%m%d-%H%M%S)
LOG="$UPD/result.log"
TAR="$INC/server.tar.gz"
SQL="$INC/upgrade.sql"

log(){ echo "[$(date +%H:%M:%S)] $*" >> "$LOG"; }

finish(){ # $1 = ok|failed
  echo "$1" > "$UPD/result.status"
  date -Iseconds > "$UPD/result.at"
  rm -f "$UPD/apply.trigger"
  chmod 644 "$UPD/result.status" "$UPD/result.at" "$LOG" 2>/dev/null || true
  exit 0
}

restore(){ # restore binaries + DB from the backup made this run, then start and report failure
  log "Rolling back to opt-$TS + db-$TS"
  systemctl stop "$SVC" 2>>"$LOG"
  rsync -a --delete "$BK/opt-$TS"/ "$OPT"/ 2>>"$LOG"
  chown -R remotesrv:remotesrv "$OPT"
  [ -f "$BK/db-$TS.sql.gz" ] && gunzip -c "$BK/db-$TS.sql.gz" | mysql remoteserver 2>>"$LOG"
  systemctl start "$SVC"
  log "Rollback finished."
  finish failed
}

health(){ # 0 if the service is active and answers /health
  for _ in $(seq 1 30); do
    sleep 2
    if systemctl is-active --quiet "$SVC" && curl -fsS -o /dev/null --max-time 3 http://127.0.0.1:5000/health; then return 0; fi
  done
  return 1
}

: > "$LOG"
log "Server update starting (ts=$TS)"
[ -f "$TAR" ] || { log "No staged server.tar.gz; nothing to do."; finish failed; }

# 1) Backup: binaries + full DB dump.
mkdir -p "$BK"; chmod 700 "$BK"
log "Backing up binaries -> $BK/opt-$TS"
cp -a "$OPT" "$BK/opt-$TS" || { log "Binary backup failed."; finish failed; }
log "Dumping database -> $BK/db-$TS.sql.gz"
if ! mysqldump --single-transaction --routines --events remoteserver 2>>"$LOG" | gzip > "$BK/db-$TS.sql.gz"; then
  log "Database dump failed; aborting before any change."; finish failed
fi
echo "$TS" > "$UPD/last_backup"; chmod 644 "$UPD/last_backup" 2>/dev/null || true
# Bound disk use: keep only the newest 3 backup sets.
ls -1dt "$BK"/opt-* 2>/dev/null | tail -n +4 | xargs -r rm -rf
ls -1t "$BK"/db-*.sql.gz 2>/dev/null | tail -n +4 | xargs -r rm -f

# 2) Stop the service.
log "Stopping $SVC"
systemctl stop "$SVC"

# 3) Optional schema upgrade (after the DB backup).
if [ -f "$SQL" ]; then
  log "Applying upgrade.sql"
  if mysql remoteserver < "$SQL" 2>>"$LOG"; then
    log "upgrade.sql applied."
  else
    log "upgrade.sql FAILED; restoring DB and aborting."
    gunzip -c "$BK/db-$TS.sql.gz" | mysql remoteserver 2>>"$LOG"
    systemctl start "$SVC"
    finish failed
  fi
fi

# 4) Extract the new build and locate the RemoteServer apphost (handles flat or nested tarballs).
STAGE=$(mktemp -d)
log "Extracting tar.gz"
if ! tar -xzf "$TAR" -C "$STAGE" 2>>"$LOG"; then log "Extract failed."; rm -rf "$STAGE"; restore; fi
APP=$(find "$STAGE" -type f -name RemoteServer | head -n1)
if [ -z "$APP" ]; then log "RemoteServer binary not found in tar.gz."; rm -rf "$STAGE"; restore; fi
SRCDIR=$(dirname "$APP")

# 5) Swap in place, preserving the prod appsettings.json.
log "Syncing new build into $OPT (preserving appsettings.json)"
rsync -a --delete --exclude appsettings.json "$SRCDIR"/ "$OPT"/ 2>>"$LOG" || { log "rsync failed."; rm -rf "$STAGE"; restore; }
chown -R remotesrv:remotesrv "$OPT"
chmod +x "$OPT/RemoteServer" 2>/dev/null || true
rm -rf "$STAGE"

# 6) Start + health-check, with auto-rollback on failure.
log "Starting $SVC"
systemctl start "$SVC"
if health; then
  log "Health check passed. Update complete."
  rm -f "$TAR" "$SQL"
  finish ok
else
  log "Health check FAILED."
  restore
fi
DEPLOY

sudo tee /opt/remoteserver-update/rollback.sh >/dev/null <<'ROLLBACK'
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
ROLLBACK

sudo chmod +x /opt/remoteserver-update/deploy.sh /opt/remoteserver-update/rollback.sh

sudo tee /etc/systemd/system/remoteserver-update.path >/dev/null <<'UNIT'
[Unit]
Description=Watch for the RemoteServer self-update trigger
[Path]
PathExists=/var/lib/remoteserver/updates/apply.trigger
Unit=remoteserver-update.service
[Install]
WantedBy=multi-user.target
UNIT
sudo tee /etc/systemd/system/remoteserver-update.service >/dev/null <<'UNIT'
[Unit]
Description=RemoteServer self-update (backup, swap, migrate, restart, auto-rollback)
[Service]
Type=oneshot
ExecStart=/opt/remoteserver-update/deploy.sh
UNIT
sudo tee /etc/systemd/system/remoteserver-rollback.path >/dev/null <<'UNIT'
[Unit]
Description=Watch for the RemoteServer rollback trigger
[Path]
PathExists=/var/lib/remoteserver/updates/rollback.trigger
Unit=remoteserver-rollback.service
[Install]
WantedBy=multi-user.target
UNIT
sudo tee /etc/systemd/system/remoteserver-rollback.service >/dev/null <<'UNIT'
[Unit]
Description=RemoteServer rollback to the last backup
[Service]
Type=oneshot
ExecStart=/opt/remoteserver-update/rollback.sh
UNIT

sudo systemctl daemon-reload
sudo systemctl enable --now remoteserver-update.path remoteserver-rollback.path
ok "self-update helper installed (update + rollback path-units enabled)"
