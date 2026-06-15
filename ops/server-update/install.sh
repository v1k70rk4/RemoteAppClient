#!/usr/bin/env bash
# One-time installer for the RemoteServer console self-update helper.
#
# Copy this folder to the server box and run as root:
#     scp -r ops/server-update root@host:/tmp/        # (or via your normal admin SSH)
#     sudo /tmp/server-update/install.sh
#
# Idempotent: safe to re-run to refresh the scripts/units after pulling a new release.
# Prerequisites: the server is already deployed (the 'remotesrv' service user and
# /opt/remoteserver exist, and the remoteserver.service systemd unit is installed).
set -euo pipefail

[ "$(id -u)" = 0 ] || { echo "Run as root: sudo $0" >&2; exit 1; }
HERE=$(cd "$(dirname "$0")" && pwd)
SVCUSER=remotesrv

id "$SVCUSER" >/dev/null 2>&1 || { echo "Service user '$SVCUSER' not found - deploy the server first." >&2; exit 1; }

# Directories: updates/incoming (service-writable) + backups (root-only) + helper dir.
install -d -o "$SVCUSER" -g "$SVCUSER" -m 755 /var/lib/remoteserver/updates /var/lib/remoteserver/updates/incoming
install -d -o root -g root -m 700 /var/lib/remoteserver/backups
install -d -o root -g root -m 755 /opt/remoteserver-update

# Scripts and units (strip any CRLF picked up on Windows; install root-owned).
for f in deploy.sh rollback.sh; do
  tmp=$(mktemp); sed 's/\r$//' "$HERE/$f" > "$tmp"
  install -o root -g root -m 755 "$tmp" "/opt/remoteserver-update/$f"; rm -f "$tmp"
done
for u in remoteserver-update.path remoteserver-update.service remoteserver-rollback.path remoteserver-rollback.service; do
  tmp=$(mktemp); sed 's/\r$//' "$HERE/$u" > "$tmp"
  install -o root -g root -m 644 "$tmp" "/etc/systemd/system/$u"; rm -f "$tmp"
done

systemctl daemon-reload
systemctl enable --now remoteserver-update.path remoteserver-rollback.path

echo "Helper installed. Path watchers:"
systemctl is-active remoteserver-update.path remoteserver-rollback.path || true
