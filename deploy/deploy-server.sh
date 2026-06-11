#!/usr/bin/env bash
# RemoteServer telepítése/frissítése: nem-root service-user, aláíró kulcs,
# systemd unit keményítéssel. A /tmp/remoteserver.tar.gz-t várja (self-contained build).
# Idempotens: újrafuttatható (frissítéshez is).
set -euo pipefail

APP_DIR="/opt/remoteserver"
ENV_DIR="/etc/remoteserver"
SVC_USER="remotesrv"
TARBALL="/tmp/remoteserver.tar.gz"

# 1) nem-root service-user
if ! id "$SVC_USER" &>/dev/null; then
  sudo useradd --system --no-create-home --shell /usr/sbin/nologin "$SVC_USER"
  echo "[deploy] service-user létrehozva: $SVC_USER"
fi

# 2) bináris
sudo systemctl stop remoteserver.service 2>/dev/null || true
sudo mkdir -p "$APP_DIR" "$ENV_DIR"
sudo rm -rf "${APP_DIR:?}"/*
sudo tar -xzf "$TARBALL" -C "$APP_DIR"
sudo chmod +x "$APP_DIR/RemoteServer"

# 3) parancs-aláíró kulcs (csak ha még nincs)
if ! sudo test -f "$ENV_DIR/cmd_signing.key"; then
  sudo openssl ecparam -name prime256v1 -genkey -noout -out "$ENV_DIR/cmd_signing.key"
  echo "[deploy] parancs-aláíró kulcs generálva."
fi
echo "[deploy] >>> AGENT CommandSigningPublicKey (Base64 SPKI) — ezt tedd az agent configba:"
sudo openssl pkey -in "$ENV_DIR/cmd_signing.key" -pubout -outform DER | base64 -w0; echo

# 3b) kliens-CA (csak ha még nincs) — a szerver csak olvassa (ProtectSystem=full miatt itt kell generálni)
if ! sudo test -f "$ENV_DIR/ca.key"; then
  sudo openssl ecparam -name prime256v1 -genkey -noout -out "$ENV_DIR/ca.key"
  sudo openssl req -new -x509 -key "$ENV_DIR/ca.key" -days 3650 \
    -subj "/CN=RemoteAppClient CA" \
    -addext "basicConstraints=critical,CA:TRUE" \
    -addext "keyUsage=critical,keyCertSign,cRLSign" \
    -out "$ENV_DIR/ca.crt"
  echo "[deploy] kliens-CA generálva."
fi
echo "[deploy] >>> CA fingerprint (az agent ezt pinneli a szerver-TLS-hez később):"
sudo openssl x509 -in "$ENV_DIR/ca.crt" -noout -fingerprint -sha256

# 4) jogosultságok: a config csak a service-useré
# (a chmod-ot root-oldalon, find-dal — a glob a hívó shellben nem fejthető ki, ha a mappa 700)
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
sudo systemctl is-active remoteserver.service && echo "[deploy] RemoteServer fut."
