# Step 12 - console-driven fleet-identity backup ("Server settings -> Backup").
#
# Why a root helper at all: the server runs as the service user and cannot read
# /etc/ssh/ssh_host_ed25519_key (root-owned 600) - and that key is exactly what every agent pins. So this
# mirrors the self-update: the server only drops a trigger file, a root oneshot does the privileged work,
# and the result lands where the server can serve it.
#
# Why it is always encrypted: this archive leaves the box through an admin console session. The fleet's
# CA private key and command-signing key cannot be revoked - rotating them means re-enrolling every
# device - so a stolen session must yield an opaque blob, never readable key material. The passphrase is
# typed by the operator in the console and never stored. (deploy/backup.sh, run locally with sudo, stays
# plain on purpose: whoever can run it already has root here, so encrypting it would add no protection,
# only a way to lock yourself out of your own disaster recovery.)
require_sudo

# Deliberately NOT under /var/lib/remoteserver/backups: that one is root:root 700 (the self-update helper
# keeps its DB dumps there), so the service user could not even traverse into a subdirectory of it and the
# trigger would never be written. This one is the service user's own.
BKC=/var/lib/remoteserver/console-backup
sudo mkdir -p /opt/remoteserver-backup "$BKC"
sudo chown "$RAC_SVC_USER:$RAC_SVC_USER" "$BKC"
sudo chmod 700 "$BKC"

# Reuse the local backup's content logic instead of duplicating the file list in a second place.
sudo cp "$HERE/backup.sh" "$HERE/lib.sh" /opt/remoteserver-backup/
sudo chmod 700 /opt/remoteserver-backup
sudo chmod 700 /opt/remoteserver-backup/backup.sh   # the helper executes this one
sudo chmod 600 /opt/remoteserver-backup/lib.sh      # only ever sourced

sudo tee /opt/remoteserver-backup/console-backup.sh >/dev/null <<'HELPER'
#!/usr/bin/env bash
# Console backup helper (root, via remoteserver-backup.service, triggered by remoteserver-backup.path
# when the server drops backup.trigger). The trigger's *content* is the operator's passphrase.
# Flow: read+shred trigger -> run backup.sh -> encrypt -> publish for download -> report.
set -uo pipefail
umask 077   # the plain archive exists for seconds; never let it be group/world readable

BKC=/var/lib/remoteserver/console-backup
TRG="$BKC/backup.trigger"
OUT="$BKC/backup.enc"
LOG="$BKC/backup.log"
SVC_USER=remotesrv

log(){ echo "[$(date +%H:%M:%S)] $*" >> "$LOG"; }

finish(){ # $1 = ok|failed
  echo "$1" > "$BKC/backup.status"
  date -Iseconds > "$BKC/backup.at"
  # The server reads these, so hand them over; the trigger must never survive (it holds the passphrase).
  shred -u "$TRG" 2>/dev/null || rm -f "$TRG"
  chown "$SVC_USER:$SVC_USER" "$BKC"/backup.* 2>/dev/null || true
  chmod 600 "$BKC"/backup.* 2>/dev/null || true
  exit 0
}

: > "$LOG"
log "Console backup starting"

PASS="$(cat "$TRG" 2>/dev/null)"
if [ -z "${PASS:-}" ]; then log "No passphrase in trigger; refusing to write an unencrypted archive."; finish failed; fi

STAGE="$(mktemp -d)"; chmod 700 "$STAGE"
cleanup(){ rm -rf "$STAGE"; }
trap cleanup EXIT

# backup.sh writes racd-identity-<ts>.tar.gz into the directory it is given.
if ! /opt/remoteserver-backup/backup.sh "$STAGE" >>"$LOG" 2>&1; then
  log "backup.sh failed - see above."; finish failed
fi
PLAIN="$(ls -1 "$STAGE"/racd-identity-*.tar.gz 2>/dev/null | head -1)"
if [ -z "$PLAIN" ]; then log "backup.sh produced no archive."; finish failed; fi

log "Encrypting (aes-256-cbc, pbkdf2)"
rm -f "$OUT"
if ! printf '%s' "$PASS" | openssl enc -aes-256-cbc -pbkdf2 -salt -in "$PLAIN" -out "$OUT" -pass stdin 2>>"$LOG"; then
  log "Encryption failed."; rm -f "$OUT"; finish failed
fi
shred -u "$PLAIN" 2>/dev/null || rm -f "$PLAIN"
unset PASS

echo "racd-identity-$(date +%Y%m%d-%H%M%S).tar.gz.enc" > "$BKC/backup.name"
log "Ready: $(du -h "$OUT" | cut -f1) encrypted archive."
finish ok
HELPER
sudo chmod 700 /opt/remoteserver-backup/console-backup.sh

sudo tee /etc/systemd/system/remoteserver-backup.path >/dev/null <<'UNIT'
[Unit]
Description=Watch for the RemoteServer console-backup trigger
[Path]
PathExists=/var/lib/remoteserver/console-backup/backup.trigger
Unit=remoteserver-backup.service
[Install]
WantedBy=multi-user.target
UNIT
sudo tee /etc/systemd/system/remoteserver-backup.service >/dev/null <<'UNIT'
[Unit]
Description=RemoteServer console backup (fleet identity, passphrase-encrypted)
[Service]
Type=oneshot
ExecStart=/opt/remoteserver-backup/console-backup.sh
UNIT

sudo systemctl daemon-reload
sudo systemctl enable --now remoteserver-backup.path
ok "console backup helper installed (path-unit enabled, archives are always encrypted)"
