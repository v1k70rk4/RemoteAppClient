#!/usr/bin/env bash
# deploy/backup.sh
# Capture everything a NEW box needs to adopt the EXISTING fleet, so replacing the OS underneath is
# invisible to the agents. Devices are never "imported": an agent's identity lives on the device
# (enrollment.json + its client cert). What it pins is what has to survive here:
#
#   commandSigningPublicKey -> cmd_signing.key        lose it: every agent rejects every command
#   bastionHostKey          -> ssh_host_ed25519_key   lose it: every reverse tunnel dies (VNC + files)
#   its own client cert     -> ca.key + ca.crt        lose it: no agent can connect at all
#
# Plus the database (devices/users/grants) and secret.key, which decrypts vnc_secret in that database.
# The fleet's whole identity is ~2 KB of secrets and one SSH host key.
#
# Deliberately NOT included:
#   db.env      - a local DB password, not fleet identity. Worse, restoring it would make step 02-mariadb
#                 return early ("db.env already exists") and never install MariaDB. Let setup.sh mint one.
#   bastion.env - 04-server regenerates it from the (restored) host key, so it rebuilds identically.
#   packages/   - re-uploadable, and large (gigabytes).
# Both env files are still archived as *.reference, for eyeballing only.
#
# Usage:  ./deploy/backup.sh [output-dir]      # default: current directory
# Restore: see deploy/restore.sh

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
source "$HERE/lib.sh"
[ -f "$HERE/config.env" ] && source "$HERE/config.env"
require_sudo

OUT_DIR="${1:-$PWD}"
[ -d "$OUT_DIR" ] || die "output directory not found: $OUT_DIR"
TS="$(date +%Y%m%d-%H%M%S)"
ARCHIVE="$OUT_DIR/racd-identity-${TS}.tar.gz"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

log "Backing up fleet identity"

# --- 1) server identity secrets -------------------------------------------------------------------
mkdir -p "$STAGE/etc-remoteserver"
for f in ca.key ca.crt cmd_signing.key secret.key agent_ca agent_ca.pub; do
  sudo test -f "$RAC_ENV_DIR/$f" || die "missing $RAC_ENV_DIR/$f - is this a RemoteServer box?"
  sudo cat "$RAC_ENV_DIR/$f" > "$STAGE/etc-remoteserver/$f"
done
for f in db.env bastion.env; do
  sudo test -f "$RAC_ENV_DIR/$f" && sudo cat "$RAC_ENV_DIR/$f" > "$STAGE/etc-remoteserver/${f}.reference"
done
ok "server secrets (6 files + 2 reference copies)"

# --- 2) the bastion SSH host key, i.e. the artefact everyone forgets -------------------------------
mkdir -p "$STAGE/etc-ssh"
sudo test -f /etc/ssh/ssh_host_ed25519_key || die "no /etc/ssh/ssh_host_ed25519_key - agents pin this key"
sudo cat /etc/ssh/ssh_host_ed25519_key     > "$STAGE/etc-ssh/ssh_host_ed25519_key"
sudo cat /etc/ssh/ssh_host_ed25519_key.pub > "$STAGE/etc-ssh/ssh_host_ed25519_key.pub"
FPR="$(sudo ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub | awk '{print $2}')"
ok "bastion host key ($FPR)"

# --- 3) database ----------------------------------------------------------------------------------
CONN="$(sudo sed -n 's/^ConnectionStrings__MariaDb=//p' "$RAC_ENV_DIR/db.env")"
[ -n "$CONN" ] || die "no connection string in $RAC_ENV_DIR/db.env"
h="$(sed -n 's/.*Server=\([^;]*\).*/\1/p'    <<<"$CONN")"
p="$(sed -n 's/.*Port=\([^;]*\).*/\1/p'      <<<"$CONN")"
u="$(sed -n 's/.*User Id=\([^;]*\).*/\1/p'   <<<"$CONN")"
pw="$(sed -n 's/.*Password=\([^;]*\).*/\1/p' <<<"$CONN")"
db="$(sed -n 's/.*Database=\([^;]*\).*/\1/p' <<<"$CONN")"
need_cmd mariadb-dump || need_cmd mysqldump || sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq mariadb-client
DUMP="$(command -v mariadb-dump || command -v mysqldump)"
MYSQL_PWD="$pw" "$DUMP" -h "${h:-localhost}" -P "${p:-3306}" -u "$u" \
  --single-transaction --routines --events --add-drop-table "$db" | gzip -9 > "$STAGE/db.sql.gz"
ok "database dump: $db ($(du -h "$STAGE/db.sql.gz" | cut -f1))"

# --- 4) manifest ----------------------------------------------------------------------------------
# The signing pubkey is recorded so a restore can be checked against what agents actually carry
# (enrollment.json -> commandSigningPublicKey). If those two ever differ, commands will be rejected.
SIGNPUB="$(sudo openssl pkey -in "$RAC_ENV_DIR/cmd_signing.key" -pubout -outform DER 2>/dev/null | base64 -w0 || echo '?')"
# The names agents actually dial come from bastion.env, not from hostname(1): on a cloud box the FQDN is
# some internal name, and telling the operator to reuse *that* would send the fleet nowhere.
PUBURL="$(sudo sed -n 's/^Server__PublicUrl=//p'    "$RAC_ENV_DIR/bastion.env" 2>/dev/null || true)"
BHOST="$(sudo sed -n 's/^Server__Bastion__Host=//p' "$RAC_ENV_DIR/bastion.env" 2>/dev/null || true)"
cat > "$STAGE/MANIFEST" <<EOF
RemoteAppClient fleet identity backup
created         : $(date -Iseconds)
public url      : ${PUBURL:-?}
bastion host    : ${BHOST:-?}
source host     : $(hostname -f 2>/dev/null || hostname) (informational; agents never use this)
database        : $db
host key (SHA256): $FPR
cmd signing pub : $SIGNPUB
contents        : etc-remoteserver/ (secrets), etc-ssh/ (bastion host key), db.sql.gz
restore         : deploy/restore.sh (phase 1) -> deploy/setup.sh -> deploy/restore.sh --db (phase 2)
EOF

# The archive carries the fleet's private keys. Tar records each member's mode, and these were staged with
# the caller's umask (world-readable), so tighten them first: extracting this anywhere - not just through
# restore.sh - must never drop world-readable private keys on disk.
chmod -R go-rwx "$STAGE"
tar -C "$STAGE" -czf "$ARCHIVE" .
chmod 600 "$ARCHIVE"
ok "archive: $ARCHIVE ($(du -h "$ARCHIVE" | cut -f1))"
warn "This file contains the private keys of the whole fleet. Store it encrypted and off this box."
info "Restore on a fresh box: ./deploy/restore.sh $(basename "$ARCHIVE")"
