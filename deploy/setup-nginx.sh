#!/usr/bin/env bash
# nginx reverse proxy TLS-terminációval a RemoteServer elé (127.0.0.1:5000).
# WebSocket-támogatás a /agent csatornához. Cert-megújuláskor auto-reload.
# Idempotens.
#
# Használat:  DOMAIN=racd.example.com ./setup-nginx.sh
set -euo pipefail

DOMAIN="${DOMAIN:?Állítsd be a DOMAIN-t, pl. DOMAIN=racd.example.com}"

sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq nginx

# A kliens-CA cert (PUBLIKUS) odamásolása, hogy az nginx olvashassa az mTLS-hez.
# (A /etc/remoteserver 700/remotesrv, ezért külön, www-data által olvasható helyre tesszük.)
if sudo test -f /etc/remoteserver/ca.crt; then
  sudo cp /etc/remoteserver/ca.crt /etc/nginx/client-ca.crt
  sudo chmod 644 /etc/nginx/client-ca.crt
else
  echo "FIGYELEM: /etc/remoteserver/ca.crt hiányzik — előbb fusson a deploy-server.sh (CA generálás)." >&2
fi

# WebSocket Upgrade-map (http kontextus, conf.d-ből töltődik)
sudo tee /etc/nginx/conf.d/ws-upgrade.conf >/dev/null <<'MAP'
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}
MAP

sudo tee "/etc/nginx/sites-available/${DOMAIN}" >/dev/null <<NGINX
server {
    listen 443 ssl http2;
    server_name ${DOMAIN};

    ssl_certificate     /etc/letsencrypt/live/${DOMAIN}/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/${DOMAIN}/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;

    # Kliens-cert (mTLS) a saját CA-nkkal. 'optional': a TLS-kézfogáskor kérjük,
    # de a publikus útvonalak (/, /enroll) cert nélkül is mennek; az agent-
    # útvonalakon a locationben kötelezővé tesszük.
    ssl_client_certificate /etc/nginx/client-ca.crt;
    ssl_verify_client optional;

    # Agent parancscsatorna (WSS) — kliens-cert KÖTELEZŐ.
    location /agent {
        if (\$ssl_client_verify != SUCCESS) { return 403; }
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header X-Client-Verify \$ssl_client_verify;
        proxy_set_header X-Client-Dn \$ssl_client_s_dn;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection \$connection_upgrade;
        proxy_read_timeout 3600s;
    }

    # Telemetria API — kliens-cert KÖTELEZŐ.
    location /api/ {
        if (\$ssl_client_verify != SUCCESS) { return 403; }
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header X-Client-Verify \$ssl_client_verify;
        proxy_set_header X-Client-Dn \$ssl_client_s_dn;
    }

    # Admin — csak localhostról (pl. SSH-alagúton át). Kintről nem elérhető.
    location /admin/ {
        allow 127.0.0.1;
        deny all;
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
    }

    # Publikus: / (állapot) és /enroll (beléptetés, még cert nélkül).
    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
    }
}
NGINX

sudo ln -sf "/etc/nginx/sites-available/${DOMAIN}" "/etc/nginx/sites-enabled/${DOMAIN}"
sudo rm -f /etc/nginx/sites-enabled/default

# Cert-megújuláskor nginx újratöltése
sudo mkdir -p /etc/letsencrypt/renewal-hooks/deploy
printf '#!/bin/sh\nsystemctl reload nginx\n' | sudo tee /etc/letsencrypt/renewal-hooks/deploy/reload-nginx.sh >/dev/null
sudo chmod +x /etc/letsencrypt/renewal-hooks/deploy/reload-nginx.sh

sudo nginx -t
sudo systemctl reload nginx
echo "[nginx] kész."
