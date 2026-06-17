# Step 04 - install RemoteServer: package, signing key, client CA, secret, bastion.env, systemd unit.
require_sudo

id "$RAC_SVC_USER" &>/dev/null || sudo useradd --system --no-create-home --shell /usr/sbin/nologin "$RAC_SVC_USER"
sudo mkdir -p "$RAC_PKG_DIR"; sudo chown -R "$RAC_SVC_USER:$RAC_SVC_USER" /var/lib/remoteserver
need_cmd wixl || sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq wixl

# obtain the self-contained server package
PKG="${RAC_PKG:-/tmp/remoteserver.tar.gz}"
if [ ! -f "$PKG" ]; then
  info "package not at $PKG - downloading the latest GitHub release"
  url="$(curl -fsSL "https://api.github.com/repos/${RAC_GH_REPO}/releases/latest" \
        | jq -r '.assets[]|select(.name=="RemoteServer-linux-x64.tar.gz")|.browser_download_url')"
  [ -n "$url" ] && [ "$url" != "null" ] || die "no RemoteServer-linux-x64.tar.gz in the latest release; set RAC_PKG to a local tarball"
  PKG="/tmp/remoteserver.tar.gz"; curl -fsSL "$url" -o "$PKG"
fi

sudo systemctl stop remoteserver.service 2>/dev/null || true
sudo mkdir -p "$RAC_APP_DIR" "$RAC_ENV_DIR"
sudo rm -rf "${RAC_APP_DIR:?}"/*
sudo tar -xzf "$PKG" -C "$RAC_APP_DIR"
sudo chmod +x "$RAC_APP_DIR/RemoteServer"

# secrets (generated once; only created when missing)
sudo test -f "$RAC_ENV_DIR/cmd_signing.key" || sudo openssl ecparam -name prime256v1 -genkey -noout -out "$RAC_ENV_DIR/cmd_signing.key"
if ! sudo test -f "$RAC_ENV_DIR/ca.key"; then
  sudo openssl ecparam -name prime256v1 -genkey -noout -out "$RAC_ENV_DIR/ca.key"
  sudo openssl req -new -x509 -key "$RAC_ENV_DIR/ca.key" -days 3650 -subj "/CN=RemoteAppClient CA" \
    -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" \
    -out "$RAC_ENV_DIR/ca.crt"
fi
sudo test -f "$RAC_ENV_DIR/secret.key" || sudo openssl rand -out "$RAC_ENV_DIR/secret.key" 32

# bastion + public-url env (machine-specific, not in the repo)
BKEY="$(sudo awk '{print $1, $2}' /etc/ssh/ssh_host_ed25519_key.pub)"
sudo tee "$RAC_ENV_DIR/bastion.env" >/dev/null <<EOF
Server__Bastion__Host=${RAC_BASTION_HOST}
Server__Bastion__HostKey=${BKEY}
Server__PublicUrl=https://${RAC_DOMAIN}
EOF

# permissions: config readable only by the service user
sudo chown -R "$RAC_SVC_USER:$RAC_SVC_USER" "$RAC_APP_DIR" "$RAC_ENV_DIR"
sudo find "$RAC_ENV_DIR" -type f -exec chmod 600 {} +
sudo chmod 700 "$RAC_ENV_DIR"

sudo tee /etc/systemd/system/remoteserver.service >/dev/null <<UNIT
[Unit]
Description=RemoteServer (RemoteAppClient C2)
After=network.target mariadb.service
Wants=mariadb.service

[Service]
Type=simple
User=${RAC_SVC_USER}
WorkingDirectory=${RAC_APP_DIR}
EnvironmentFile=${RAC_ENV_DIR}/db.env
EnvironmentFile=-${RAC_ENV_DIR}/bastion.env
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
ExecStart=${RAC_APP_DIR}/RemoteServer
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
if sudo systemctl is-active --quiet remoteserver.service; then ok "RemoteServer running"; else warn "RemoteServer not active - check: sudo journalctl -u remoteserver -n 50"; fi
info ">>> agent CommandSigningPublicKey (base64 SPKI), for reference:"
sudo openssl pkey -in "$RAC_ENV_DIR/cmd_signing.key" -pubout -outform DER | base64 -w0; echo
