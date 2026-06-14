#!/usr/bin/env bash
# Installs/updates RemoteServer with a non-root service user, signing keys,
# and a hardened systemd unit. Expects /tmp/remoteserver.tar.gz (self-contained build).
# Idempotent: can be re-run for updates too.
set -euo pipefail

APP_DIR="/opt/remoteserver"
ENV_DIR="/etc/remoteserver"
SVC_USER="remotesrv"
TARBALL="/tmp/remoteserver.tar.gz"

# 1) non-root service user
if ! id "$SVC_USER" &>/dev/null; then
  sudo useradd --system --no-create-home --shell /usr/sbin/nologin "$SVC_USER"
  echo "[deploy] service user created: $SVC_USER"
fi

# 1b) update package directory (separate under /var/lib, survives redeploys)
sudo mkdir -p /var/lib/remoteserver/packages
sudo chown -R "$SVC_USER:$SVC_USER" /var/lib/remoteserver

# 1c) wixl (msitools) for MSI building, when missing
if ! command -v wixl >/dev/null 2>&1; then
  sudo apt-get update -qq && sudo apt-get install -y wixl
  echo "[deploy] wixl installed (MSI building)."
fi

# 2) binaries
sudo systemctl stop remoteserver.service 2>/dev/null || true
sudo mkdir -p "$APP_DIR" "$ENV_DIR"
sudo rm -rf "${APP_DIR:?}"/*
sudo tar -xzf "$TARBALL" -C "$APP_DIR"
sudo chmod +x "$APP_DIR/RemoteServer"

# 3) command-signing key (only when missing)
if ! sudo test -f "$ENV_DIR/cmd_signing.key"; then
  sudo openssl ecparam -name prime256v1 -genkey -noout -out "$ENV_DIR/cmd_signing.key"
  echo "[deploy] command-signing key generated."
fi
echo "[deploy] >>> AGENT CommandSigningPublicKey (Base64 SPKI) - put this into the agent config:"
sudo openssl pkey -in "$ENV_DIR/cmd_signing.key" -pubout -outform DER | base64 -w0; echo

# 3b) client CA (only when missing). The server only reads it, so generate it here
# because the systemd unit uses ProtectSystem=full.
if ! sudo test -f "$ENV_DIR/ca.key"; then
  sudo openssl ecparam -name prime256v1 -genkey -noout -out "$ENV_DIR/ca.key"
  sudo openssl req -new -x509 -key "$ENV_DIR/ca.key" -days 3650 \
    -subj "/CN=RemoteAppClient CA" \
    -addext "basicConstraints=critical,CA:TRUE" \
    -addext "keyUsage=critical,keyCertSign,cRLSign" \
    -out "$ENV_DIR/ca.crt"
  echo "[deploy] client CA generated."
fi
echo "[deploy] >>> CA fingerprint (the agent pins this for server TLS later):"
sudo openssl x509 -in "$ENV_DIR/ca.crt" -noout -fingerprint -sha256

# 3c) bastion config env. Host comes from BASTION_HOST; host key from the box.
# Included in enrollment responses. Machine-specific, so it does not live in the repo.
if [ -f /etc/ssh/ssh_host_ed25519_key.pub ]; then
  BKEY="$(sudo awk '{print $1, $2}' /etc/ssh/ssh_host_ed25519_key.pub)"
  sudo tee "$ENV_DIR/bastion.env" >/dev/null <<EOF
Server__Bastion__Host=${BASTION_HOST:-}
Server__Bastion__HostKey=${BKEY}
EOF
  echo "[deploy] bastion env written (Host='${BASTION_HOST:-<empty>}')."
fi

# 3d) DB secret encryption key (32 bytes) for vnc_secret encryption at rest
if ! sudo test -f "$ENV_DIR/secret.key"; then
  sudo openssl rand -out "$ENV_DIR/secret.key" 32
  echo "[deploy] secret.key generated."
fi

# 4) permissions: config belongs only to the service user
# Run chmod from the root side with find; caller-side globs cannot expand when the directory is 700.
sudo chown -R "$SVC_USER:$SVC_USER" "$APP_DIR" "$ENV_DIR"
sudo find "$ENV_DIR" -type f -exec chmod 600 {} +
sudo chmod 700 "$ENV_DIR"

# 5) systemd unit
sudo tee /etc/systemd/system/remoteserver.service >/dev/null <<UNIT
[Unit]
Description=RemoteServer (RemoteAppClient C2)
After=network.target mariadb.service
Wants=mariadb.service

[Service]
Type=simple
User=${SVC_USER}
WorkingDirectory=${APP_DIR}
EnvironmentFile=${ENV_DIR}/db.env
EnvironmentFile=-${ENV_DIR}/bastion.env
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
ExecStart=${APP_DIR}/RemoteServer
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
ProtectSystem=full
ProtectHome=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
UNIT

sudo systemctl daemon-reload
sudo systemctl enable --now remoteserver.service
sleep 3
sudo systemctl is-active remoteserver.service && echo "[deploy] RemoteServer is running."
