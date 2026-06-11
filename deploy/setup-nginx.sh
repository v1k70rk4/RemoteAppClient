#!/usr/bin/env bash
# nginx reverse proxy TLS-terminációval a RemoteServer elé (127.0.0.1:5000).
# WebSocket-támogatás a /agent csatornához. Cert-megújuláskor auto-reload.
# Idempotens.
#
# Használat:  DOMAIN=racd.example.com ./setup-nginx.sh
set -euo pipefail

DOMAIN="${DOMAIN:?Állítsd be a DOMAIN-t, pl. DOMAIN=racd.example.com}"

sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq nginx

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

    # mTLS (enrollment/CA után): ssl_client_certificate <CA>; ssl_verify_client on;
    # és a /agent + /api/telemetry blokkban a CN továbbadása headerben a backendnek.

    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection \$connection_upgrade;
        proxy_read_timeout 3600s;
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
