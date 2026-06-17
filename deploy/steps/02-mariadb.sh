# Step 02 - MariaDB: use an existing server, or install one locally. Writes db.env.
# Asked at the very start of a full run so the rest is unattended.
require_sudo
ENV_FILE="$RAC_ENV_DIR/db.env"
sudo mkdir -p "$RAC_ENV_DIR"

if sudo test -f "$ENV_FILE"; then
  ok "db.env already exists - reusing (delete it to reconfigure)"
  return 0
fi

DB_NAME="${RAC_DB_NAME:-remoteserver}"

if [ -n "${RAC_DB_CONN:-}" ]; then
  CONN="$RAC_DB_CONN"
  info "using RAC_DB_CONN from config.env"
elif ask_yn "Do you already have a MariaDB/MySQL server to use?" "n"; then
  db_host="$(ask 'DB host' '127.0.0.1')"
  db_port="$(ask 'DB port' '3306')"
  DB_NAME="$(ask 'Database name' "$DB_NAME")"
  db_user="$(ask 'DB user' 'remoteserver')"
  db_pass="$(ask_secret 'DB password')"
  CONN="Server=${db_host};Port=${db_port};Database=${DB_NAME};User Id=${db_user};Password=${db_pass}"
  warn "Ensure that database + user exist and the user may CREATE TABLE (step 03 loads the schema)."
else
  info "installing MariaDB locally..."
  sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq mariadb-server
  sudo systemctl enable --now mariadb
  db_user="remoteserver"
  db_pass="$(openssl rand -base64 24 | tr -dc 'A-Za-z0-9' | head -c 32)"
  sudo mariadb <<SQL
CREATE DATABASE IF NOT EXISTS \`${DB_NAME}\` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER IF NOT EXISTS '${db_user}'@'localhost' IDENTIFIED BY '${db_pass}';
ALTER USER '${db_user}'@'localhost' IDENTIFIED BY '${db_pass}';
GRANT ALL PRIVILEGES ON \`${DB_NAME}\`.* TO '${db_user}'@'localhost';
FLUSH PRIVILEGES;
SQL
  CONN="Server=localhost;Port=3306;Database=${DB_NAME};User Id=${db_user};Password=${db_pass}"
  ok "MariaDB installed; database '${DB_NAME}' + user '${db_user}' created"
fi

printf 'ConnectionStrings__MariaDb=%s\n' "$CONN" | sudo tee "$ENV_FILE" >/dev/null
sudo chmod 600 "$ENV_FILE"; sudo chown root:root "$ENV_FILE"
export RAC_DB_NAME="$DB_NAME"
ok "db.env written ($ENV_FILE, root-only)"
