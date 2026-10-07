#!/usr/bin/env bash
# deploy/lib.sh - shared helpers, sourced by setup.sh and every steps/*.sh.
# Not meant to run on its own.

log()  { printf '\n\033[1;36m== %s\033[0m\n' "$*"; }
info() { printf '   %s\n' "$*"; }
ok()   { printf '   \033[1;32mok\033[0m  %s\n' "$*"; }
warn() { printf '   \033[1;33m!!\033[0m  %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[fatal]\033[0m %s\n' "$*" >&2; exit 1; }

# ask "Question" "default" -> echoes the answer (or default; default on non-interactive).
ask() {
  local prompt="$1" def="${2:-}" ans
  if [ -n "${RAC_NONINTERACTIVE:-}" ] || [ ! -t 0 ]; then printf '%s' "$def"; return; fi
  read -rp "   $prompt${def:+ [$def]}: " ans
  printf '%s' "${ans:-$def}"
}
# ask_secret "Prompt" -> echoes the typed secret (no echo on screen).
ask_secret() {
  local prompt="$1" ans
  read -rsp "   $prompt: " ans; printf '\n' >&2
  printf '%s' "$ans"
}
# ask_yn "Question?" "default(y/n)" -> exit 0 = yes.
ask_yn() {
  local ans; ans="$(ask "$1 (y/n)" "${2:-n}")"
  [[ "$ans" =~ ^[Yy] ]]
}

need_cmd()     { command -v "$1" >/dev/null 2>&1; }
require_sudo() { sudo -n true 2>/dev/null || die "passwordless sudo required (run as a user with NOPASSWD sudo)"; }
# Schema changes (the schema load, a restore) need rights the application's role no longer has.
#   - RAC_DB_ADMIN_CONN set: an administrative connection string for an external database, same format as
#     RAC_DB_CONN, with DDL rights; it is never written to the box.
#   - a local MariaDB: root over the unix socket, as the update helper does.
#   - otherwise: the application's own credentials ($h, $p, $u, $pw from db.env, set by the caller), which
#     then have to carry DDL rights themselves - said once, so a DML-only account fails with a reason.
# shellcheck disable=SC2154
db_admin() {
  if [ -n "${RAC_DB_ADMIN_CONN:-}" ]; then
    local ah ap au apw
    ah="$(sed -n 's/.*Server=\([^;]*\).*/\1/p'   <<<"$RAC_DB_ADMIN_CONN")"
    ap="$(sed -n 's/.*Port=\([^;]*\).*/\1/p'     <<<"$RAC_DB_ADMIN_CONN")"
    au="$(sed -n 's/.*User Id=\([^;]*\).*/\1/p'  <<<"$RAC_DB_ADMIN_CONN")"
    apw="$(sed -n 's/.*Password=\([^;]*\).*/\1/p' <<<"$RAC_DB_ADMIN_CONN")"
    MYSQL_PWD="$apw" mariadb -h "${ah:-localhost}" -P "${ap:-3306}" -u "$au" "$@"
  elif [ -z "${h:-}" ] || [ "$h" = localhost ] || [ "$h" = 127.0.0.1 ]; then
    sudo mariadb "$@"
  else
    [ -n "${RAC_DB_ADMIN_WARNED:-}" ] || { warn "external database: schema changes run with the application's credentials ($u@$h); they need DDL rights, or set RAC_DB_ADMIN_CONN"; RAC_DB_ADMIN_WARNED=1; }
    MYSQL_PWD="$pw" mariadb -h "$h" -P "${p:-3306}" -u "$u" "$@"
  fi
}

# Best-effort default DNS name: the box FQDN, else reverse-DNS of the primary IP (DNS only,
# no external service), else a clearly-fake placeholder. The user can always override at the prompt.
default_domain() {
  local fqdn ip rdns
  fqdn="$(hostname --fqdn 2>/dev/null || true)"
  case "$fqdn" in *.*.*) printf '%s' "$fqdn"; return ;; esac
  ip="$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{print $7; exit}')"
  [ -z "$ip" ] && ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
  if [ -n "$ip" ]; then
    rdns="$(getent hosts "$ip" 2>/dev/null | awk '{print $2; exit}')"
    [ -z "$rdns" ] && command -v dig >/dev/null 2>&1 && rdns="$(dig +short -x "$ip" 2>/dev/null | sed 's/\.$//;q')"
    case "$rdns" in *.*) printf '%s' "$rdns"; return ;; esac
  fi
  printf 'racd.temp.tmp'
}

# Shared paths / names (override via config.env if you must).
: "${RAC_APP_DIR:=/opt/remoteserver}"
: "${RAC_ENV_DIR:=/etc/remoteserver}"
: "${RAC_SVC_USER:=remotesrv}"
: "${RAC_AGENT_USER:=agent}"
: "${RAC_PKG_DIR:=/var/lib/remoteserver/packages}"
: "${RAC_GH_REPO:=v1k70rk4/RemoteAppClient}"
