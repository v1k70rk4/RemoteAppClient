# Step 03 - load the baseline schema into the database (derives the connection from db.env).
require_sudo
SCHEMA="${RAC_SCHEMA:-$REPO_ROOT/src/RemoteServer/Data/Migrations/schema.sql}"
if [ ! -f "$SCHEMA" ]; then
  info "schema.sql not found locally - downloading from GitHub"
  SCHEMA="/tmp/rac-schema.sql"
  curl -fsSL "https://raw.githubusercontent.com/${RAC_GH_REPO}/master/src/RemoteServer/Data/Migrations/schema.sql" -o "$SCHEMA" \
    || die "could not obtain schema.sql"
fi
need_cmd mariadb || sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq mariadb-client

CONN="$(sudo sed -n 's/^ConnectionStrings__MariaDb=//p' "$RAC_ENV_DIR/db.env")"
[ -n "$CONN" ] || die "db.env has no connection string - run 02-mariadb first"
h="$(sed -n 's/.*Server=\([^;]*\).*/\1/p'   <<<"$CONN")"
p="$(sed -n 's/.*Port=\([^;]*\).*/\1/p'     <<<"$CONN")"
u="$(sed -n 's/.*User Id=\([^;]*\).*/\1/p'  <<<"$CONN")"
pw="$(sed -n 's/.*Password=\([^;]*\).*/\1/p' <<<"$CONN")"
db="$(sed -n 's/.*Database=\([^;]*\).*/\1/p' <<<"$CONN")"

count_tables() {
  MYSQL_PWD="$pw" mariadb -N -B -h "${h:-localhost}" -P "${p:-3306}" -u "$u" \
    -e "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='$db';" 2>/dev/null || echo 0
}
existing="$(count_tables)"
if [ "${existing:-0}" -gt 0 ] && [ -z "${RAC_SCHEMA_FORCE:-}" ]; then
  ok "schema already present in '$db' ($existing table(s)) - skipping (set RAC_SCHEMA_FORCE=1 to drop & reload)"
else
  if [ "${existing:-0}" -gt 0 ]; then
    warn "RAC_SCHEMA_FORCE set - dropping $existing existing table(s) in '$db'"
    drops="$(MYSQL_PWD="$pw" mariadb -N -B -h "${h:-localhost}" -P "${p:-3306}" -u "$u" \
      -e "SELECT CONCAT('DROP TABLE IF EXISTS \`', table_name, '\`;') FROM information_schema.tables WHERE table_schema='$db';" 2>/dev/null)"
    MYSQL_PWD="$pw" mariadb -h "${h:-localhost}" -P "${p:-3306}" -u "$u" "$db" \
      -e "SET FOREIGN_KEY_CHECKS=0; ${drops} SET FOREIGN_KEY_CHECKS=1;"
  fi
  MYSQL_PWD="$pw" mariadb -h "${h:-localhost}" -P "${p:-3306}" -u "$u" "$db" < "$SCHEMA"
  ok "schema loaded into '$db'"
fi
