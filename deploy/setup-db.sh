#!/usr/bin/env bash
# Creates the MariaDB database and application user for RemoteServer.
# Idempotent: can be re-run. The password is generated on the machine and written
# only to a root-readable env file (/etc/remoteserver/db.env).
set -euo pipefail

DB_NAME="remoteserver"
DB_USER="remoteserver"
ENV_DIR="/etc/remoteserver"
ENV_FILE="${ENV_DIR}/db.env"

sudo mkdir -p "$ENV_DIR"

if sudo test -f "$ENV_FILE"; then
  echo "[setup-db] $ENV_FILE already exists - reusing password."
  DB_PASS="$(sudo sed -n 's/.*Password=\([^;]*\).*/\1/p' "$ENV_FILE")"
else
  DB_PASS="$(openssl rand -base64 24 | tr -dc 'A-Za-z0-9' | head -c 32)"
  echo "[setup-db] new DB password generated."
fi

sudo mariadb <<SQL
CREATE DATABASE IF NOT EXISTS \`${DB_NAME}\` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER IF NOT EXISTS '${DB_USER}'@'localhost' IDENTIFIED BY '${DB_PASS}';
ALTER USER '${DB_USER}'@'localhost' IDENTIFIED BY '${DB_PASS}';
GRANT ALL PRIVILEGES ON \`${DB_NAME}\`.* TO '${DB_USER}'@'localhost';
FLUSH PRIVILEGES;
SQL

CONN="Server=localhost;Port=3306;Database=${DB_NAME};User Id=${DB_USER};Password=${DB_PASS}"
printf 'ConnectionStrings__MariaDb=%s\n' "$CONN" | sudo tee "$ENV_FILE" >/dev/null
sudo chmod 600 "$ENV_FILE"
sudo chown root:root "$ENV_FILE"
echo "[setup-db] done. Env: $ENV_FILE (root-only)."
