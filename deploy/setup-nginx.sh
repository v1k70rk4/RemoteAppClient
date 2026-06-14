#!/usr/bin/env bash
# nginx reverse proxy with TLS termination in front of RemoteServer (127.0.0.1:5000).
# WebSocket support for the /agent channel. Auto-reload after certificate renewal.
# Idempotent.
#
# Usage: DOMAIN=racd.example.com ./setup-nginx.sh
set -euo pipefail

DOMAIN="${DOMAIN:?Set DOMAIN, for example DOMAIN=racd.example.com}"

sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq nginx

# Copy the client CA certificate (public) so nginx can read it for mTLS.
# /etc/remoteserver is 700/remotesrv, so keep a separate www-data-readable copy.
if sudo test -f /etc/remoteserver/ca.crt; then
  sudo cp /etc/remoteserver/ca.crt /etc/nginx/client-ca.crt
  sudo chmod 644 /etc/nginx/client-ca.crt
else
  echo "WARNING: /etc/remoteserver/ca.crt is missing - run deploy-server.sh first (CA generation)." >&2
fi

# WebSocket Upgrade map (http context, loaded from conf.d)
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

    # Client certificate (mTLS) with our CA. 'optional' asks for it during TLS,
    # while public routes (/, /enroll) still work without a cert; agent routes
    # make it mandatory in their location blocks.
    ssl_client_certificate /etc/nginx/client-ca.crt;
    ssl_verify_client optional;

    # Agent command channel (WSS) - client certificate REQUIRED.
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

    # Telemetry API - client certificate REQUIRED.
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

    # Admin - localhost only, for example through an SSH tunnel. Not reachable externally.
    location /admin/ {
        allow 127.0.0.1;
        deny all;
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
    }

    # Public: / (status) and /enroll (enrollment before a client certificate exists).
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

# Reload nginx after certificate renewal.
sudo mkdir -p /etc/letsencrypt/renewal-hooks/deploy
printf '#!/bin/sh\nsystemctl reload nginx\n' | sudo tee /etc/letsencrypt/renewal-hooks/deploy/reload-nginx.sh >/dev/null
sudo chmod +x /etc/letsencrypt/renewal-hooks/deploy/reload-nginx.sh

sudo nginx -t
sudo systemctl reload nginx
echo "[nginx] done."
