#!/usr/bin/env bash
# deploy/setup.sh
# Bring a FRESH Ubuntu box to a working RemoteServer from zero: database, server,
# bastion, TLS, nginx, hardening, and the first bootstrap blob - then verify it.
#
# Run LOCALLY on the target server, as a user with passwordless sudo (no SSH needed):
#     ./deploy/setup.sh                  # full run, asks what it needs
#     ./deploy/setup.sh 02-mariadb       # run a single step
#     ./deploy/setup.sh 06-tls 07-nginx  # run a subset, in order
#
# Unattended: cp config.env.example config.env, fill it in, then run.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/.." && pwd)"
export HERE REPO_ROOT
# shellcheck source=lib.sh
source "$HERE/lib.sh"
[ -f "$HERE/config.env" ] && { info "loading config.env"; source "$HERE/config.env"; }

require_sudo

# --- configuration collected up front so the rest runs unattended ---
log "Configuration"
RAC_DOMAIN="${RAC_DOMAIN:-$(ask 'Public DNS name of this server' "$(default_domain)")}"
RAC_BASTION_HOST="${RAC_BASTION_HOST:-$(ask 'Bastion host (where agents SSH)' "$RAC_DOMAIN")}"
RAC_ACME_EMAIL="${RAC_ACME_EMAIL:-$(ask 'ACME email (Lets Encrypt)' "admin@${RAC_DOMAIN#*.}")}"
export RAC_DOMAIN RAC_BASTION_HOST RAC_ACME_EMAIL
info "domain=$RAC_DOMAIN | bastion=$RAC_BASTION_HOST | acme=$RAC_ACME_EMAIL"

ALL_STEPS=(01-prereqs 02-mariadb 03-schema 04-server 05-bastion 06-tls 07-nginx 08-harden 09-blob 10-selfupdate 12-backup 11-verify)
if [ "$#" -gt 0 ]; then RUN=("$@"); else RUN=("${ALL_STEPS[@]}"); fi

for step in "${RUN[@]}"; do
  step="${step%.sh}"
  f="$HERE/steps/${step}.sh"
  [ -f "$f" ] || die "unknown step '$step' (have: ${ALL_STEPS[*]})"
  log "Step ${step}"
  # shellcheck disable=SC1090
  source "$f"
done

log "Finished."
info "Next: grab the bootstrap blob printed by step 09-blob and enroll the first Windows device (see deploy/README.md)."
