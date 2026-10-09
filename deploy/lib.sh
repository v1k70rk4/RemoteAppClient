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
# Schema changes (the schema load, a restore) need rights the application's role no longer has. In order:
#   - RAC_DB_LOCAL=1 in db.env (written by 02-mariadb when it installed MariaDB on this box): root over the unix
#     socket, as the update helper does;
#   - RAC_DB_ADMIN_CONN: an administrative connection for an external database, same format as RAC_DB_CONN;
#   - a loopback host while this box runs a mariadb service: an installation from before the marker existed;
#   - otherwise the application's own credentials ($h, $p, $u, $pw from db.env, set by the caller), which then
#     have to carry DDL rights themselves - said once, so a DML-only account fails with a reason.
# shellcheck disable=SC2154
# --sandbox (MariaDB client 10.11.7+): a "\!" or "system" line in an SQL file cannot start a shell.
db_sandbox() { mariadb --help 2>/dev/null | grep -q -- '--sandbox' && printf -- '--sandbox'; }
db_admin() {
  if sudo grep -qs '^RAC_DB_LOCAL=1' "${RAC_ENV_DIR:-/etc/remoteserver}/db.env"; then sudo mariadb $(db_sandbox) "$@"; return; fi
  if [ -n "${RAC_DB_ADMIN_CONN:-}" ]; then db_client_with "$RAC_DB_ADMIN_CONN" "$@"; return; fi
  if { [ -z "${h:-}" ] || [ "$h" = localhost ] || [ "$h" = 127.0.0.1 ]; } && systemctl is-active --quiet mariadb 2>/dev/null; then
    sudo mariadb $(db_sandbox) "$@"; return
  fi
  [ -n "${RAC_DB_ADMIN_WARNED:-}" ] || { warn "external database: schema changes run with the application's credentials ($u@$h); they need DDL rights, or set RAC_DB_ADMIN_CONN"; RAC_DB_ADMIN_WARNED=1; }
  db_client_with "Server=${h:-localhost};Port=${p:-3306};User Id=$u;Password=$pw" "$@"
}

# Runs the mariadb client with the credentials of a connection string. The password goes in a mode-0600 option
# file that lives only for the call - not in the environment, which every child process would inherit and
# which is readable through /proc while the client runs. Inside the quoted option-file value a backslash and a
# double quote are escape characters, so they are doubled and escaped first; everything else is read verbatim.
db_client_with() {
  local conn="$1"; shift
  local ch cp cu cpw opt rc
  ch="$(sed -n 's/.*Server=\([^;]*\).*/\1/p'    <<<"$conn")"
  cp="$(sed -n 's/.*Port=\([^;]*\).*/\1/p'      <<<"$conn")"
  cu="$(sed -n 's/.*User Id=\([^;]*\).*/\1/p'   <<<"$conn")"
  cpw="$(sed -n 's/.*Password=\([^;]*\).*/\1/p' <<<"$conn")"
  cpw="${cpw//\\/\\\\}"; cpw="${cpw//\"/\\\"}"
  opt="$(mktemp)"; chmod 600 "$opt"
  printf '[client]\nhost=%s\nport=%s\nuser=%s\npassword="%s"\n' "${ch:-localhost}" "${cp:-3306}" "$cu" "$cpw" > "$opt"
  mariadb $(db_sandbox) --defaults-extra-file="$opt" "$@" && rc=0 || rc=$?
  rm -f "$opt"
  return "$rc"
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
