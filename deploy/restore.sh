#!/usr/bin/env bash
# deploy/restore.sh
# Adopt an existing fleet on a fresh box, so an OS swap is invisible to the agents.
#
# Three commands, in this order - setup.sh has to run in the middle:
#
#   1. ./deploy/restore.sh racd-identity-*.tar.gz         # secrets + bastion host key
#   2. ./deploy/setup.sh                                  # finds the secrets and REUSES them
#   3. ./deploy/restore.sh racd-identity-*.tar.gz --db    # load the database, restart the server
#
# Why that order. 04-server only generates secrets that are MISSING, so putting them back first makes the
# installer adopt them instead of minting new ones (new ones = every agent locked out). It also rebuilds
# bastion.env from whatever /etc/ssh/ssh_host_ed25519_key.pub says at that moment - restore the host key
# first and it rebuilds identically; skip it and every agent's pinned key mismatches, killing all tunnels.
#
# The database is loaded last because 02-mariadb must install MariaDB and mint a fresh db.env first. The
# old db.env is deliberately NOT restored: it is a local password, not fleet identity, and its presence
# would make 02-mariadb return early and never install the database at all.

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
source "$HERE/lib.sh"
[ -f "$HERE/config.env" ] && source "$HERE/config.env"
require_sudo

ARCHIVE="${1:-}"
MODE="${2:-secrets}"
[ -n "$ARCHIVE" ] || die "usage: ./deploy/restore.sh <racd-identity-*.tar.gz> [--db]"
[ -f "$ARCHIVE" ] || die "archive not found: $ARCHIVE"
[ "$MODE" = "--db" ] && MODE="db"

STAGE="$(mktemp -d)"; chmod 700 "$STAGE"
trap 'rm -rf "$STAGE"' EXIT
tar -xzf "$ARCHIVE" -C "$STAGE"
[ -f "$STAGE/MANIFEST" ] || die "not a fleet-identity archive (no MANIFEST)"

log "Fleet identity archive"
cat "$STAGE/MANIFEST" | sed 's/^/   /'

# ==================================================================================================
if [ "$MODE" = "db" ]; then
  log "Phase 2 - database"
  sudo test -f "$RAC_ENV_DIR/db.env" || die "no $RAC_ENV_DIR/db.env yet - run ./deploy/setup.sh first"
  [ -f "$STAGE/db.sql.gz" ] || die "archive has no db.sql.gz"

  CONN="$(sudo sed -n 's/^ConnectionStrings__MariaDb=//p' "$RAC_ENV_DIR/db.env")"
  h="$(sed -n 's/.*Server=\([^;]*\).*/\1/p'    <<<"$CONN")"
  p="$(sed -n 's/.*Port=\([^;]*\).*/\1/p'      <<<"$CONN")"
  u="$(sed -n 's/.*User Id=\([^;]*\).*/\1/p'   <<<"$CONN")"
  pw="$(sed -n 's/.*Password=\([^;]*\).*/\1/p' <<<"$CONN")"
  db="$(sed -n 's/.*Database=\([^;]*\).*/\1/p' <<<"$CONN")"
  need_cmd mariadb || sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq mariadb-client

  warn "This replaces the contents of database '$db' on this box."
  ask_yn "Load the dump into '$db'?" "y" || die "aborted"

  sudo systemctl stop remoteserver.service 2>/dev/null || true
  gunzip -c "$STAGE/db.sql.gz" | MYSQL_PWD="$pw" mariadb -h "${h:-localhost}" -P "${p:-3306}" -u "$u" "$db"
  ok "database loaded"

  sudo systemctl start remoteserver.service
  sleep 3
  n="$(MYSQL_PWD="$pw" mariadb -N -B -h "${h:-localhost}" -P "${p:-3306}" -u "$u" \
        -e "SELECT COUNT(*) FROM \`$db\`.Devices;" 2>/dev/null || echo '?')"
  ok "$n device(s) restored"
  if curl -fsS --max-time 5 http://127.0.0.1:5000/health >/dev/null 2>&1; then ok "server healthy"; else warn "server not healthy - check: sudo journalctl -u remoteserver -n 50"; fi
  log "Done. Agents should reconnect on their own; watch the fleet list."
  exit 0
fi

# ==================================================================================================
log "Phase 1 - secrets + bastion host key"

# --- bastion SSH host key: must be in place BEFORE 04-server bakes it into bastion.env ------------
OLD_FPR="$(sudo ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub 2>/dev/null | awk '{print $2}' || echo 'none')"
NEW_FPR="$(ssh-keygen -lf "$STAGE/etc-ssh/ssh_host_ed25519_key.pub" | awk '{print $2}')"
info "this box : $OLD_FPR"
info "archive  : $NEW_FPR"
if [ "$OLD_FPR" = "$NEW_FPR" ]; then
  ok "host key already matches the archive - nothing to do"
else
  warn "Replacing this box's SSH host key with the fleet's. Agents pin it, so this is the point."
  warn "Your current SSH session survives; new connections will see the restored key (clients that"
  warn "already knew THIS box's key will warn until their known_hosts entry is dropped)."
  ask_yn "Replace the SSH host key?" "y" || die "aborted"
  sudo cp "$STAGE/etc-ssh/ssh_host_ed25519_key"     /etc/ssh/ssh_host_ed25519_key
  sudo cp "$STAGE/etc-ssh/ssh_host_ed25519_key.pub" /etc/ssh/ssh_host_ed25519_key.pub
  sudo chown root:root /etc/ssh/ssh_host_ed25519_key /etc/ssh/ssh_host_ed25519_key.pub
  sudo chmod 600 /etc/ssh/ssh_host_ed25519_key
  sudo chmod 644 /etc/ssh/ssh_host_ed25519_key.pub
  sudo sshd -t || die "sshd rejected the restored host key - NOT restarting ssh"
  sudo systemctl restart ssh
  ok "host key restored ($NEW_FPR) and ssh restarted"
fi

# --- server identity secrets: 04-server only creates what is missing, so it will adopt these -------
sudo mkdir -p "$RAC_ENV_DIR"
for f in ca.key ca.crt cmd_signing.key secret.key agent_ca agent_ca.pub; do
  [ -f "$STAGE/etc-remoteserver/$f" ] || die "archive is missing $f"
  if sudo test -f "$RAC_ENV_DIR/$f"; then
    warn "$RAC_ENV_DIR/$f already exists - overwriting with the archived one"
  fi
  sudo cp "$STAGE/etc-remoteserver/$f" "$RAC_ENV_DIR/$f"
done
# root-owned for now; 04-server chowns the whole directory to the service user.
sudo chown -R root:root "$RAC_ENV_DIR"
sudo find "$RAC_ENV_DIR" -type f -exec chmod 600 {} +
sudo chmod 700 "$RAC_ENV_DIR"
ok "server secrets restored into $RAC_ENV_DIR"

log "Phase 1 done."
info "Next:"
info "  1. ./deploy/setup.sh                       # it will REUSE the restored secrets"
info "  2. ./deploy/restore.sh $(basename "$ARCHIVE") --db"
warn "Keep the same public name as before ($(sed -n 's/^public url *: //p' "$STAGE/MANIFEST")) - agents dial it by name."
