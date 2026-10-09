# Step 07 - nginx 443 multiplexer (SSH + HTTPS share the port; real client IP preserved), like prod.
#   public 443 -> stream ssl_preread -> TLS to 127.0.0.1:8443 (http vhost, PROXY protocol)
#                                       SSH to 127.0.0.1:2222 (strip-relay) -> sshd:22
# So the agent's ssl443 transport AND direct SSH both reach the box on 443; HTTPS/WSS too.
require_sudo
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq nginx libnginx-mod-stream
sudo test -f "$RAC_ENV_DIR/ca.crt" || die "client CA missing - run 04-server first"
sudo cp "$RAC_ENV_DIR/ca.crt" /etc/nginx/client-ca.crt; sudo chmod 644 /etc/nginx/client-ca.crt

sudo tee /etc/nginx/conf.d/ws-upgrade.conf >/dev/null <<'MAP'
map $http_upgrade $connection_upgrade { default upgrade; '' close; }
MAP

# The proxy secret: nginx sends it in X-RAC-Proxy on every request, and the server believes the certificate
# identity and real-IP headers only on requests that carry it - Kestrel's loopback port is also reachable
# through the bastion's forwards. Generated once, kept across re-runs; hex, so it is safe inside nginx quotes.
PROXY_ENV="$RAC_ENV_DIR/proxy.env"
if ! sudo test -s "$PROXY_ENV"; then
  printf 'Server__ProxySecret=%s\n' "$(openssl rand -hex 32)" | sudo tee "$PROXY_ENV" >/dev/null
fi
sudo chmod 600 "$PROXY_ENV"
PROXY_SECRET="$(sudo sed -n 's/^Server__ProxySecret=//p' "$PROXY_ENV")"
[ -n "$PROXY_SECRET" ] || die "proxy secret missing in $PROXY_ENV"

# HTTP vhost on loopback 8443, fed by the stream mux over PROXY protocol (real IP via real_ip).
sudo tee "/etc/nginx/sites-available/${RAC_DOMAIN}" >/dev/null <<NGINX
server {
    listen 127.0.0.1:8443 ssl http2 proxy_protocol;
    server_name ${RAC_DOMAIN};

    set_real_ip_from 127.0.0.1;
    real_ip_header proxy_protocol;

    ssl_certificate     /etc/letsencrypt/live/${RAC_DOMAIN}/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/${RAC_DOMAIN}/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_client_certificate /etc/nginx/client-ca.crt;
    ssl_verify_client optional;
    # The agents and consoles pin the certificate anyway; HSTS is for a browser that opens this host.
    add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;

    # Agent command channel (WSS) + SSH-over-WebSocket: client certificate REQUIRED.
    # Locations match case-insensitively (~*) because the server's routing does: a case-sensitive match
    # would let /API/... fall through to "location /" without the certificate check.
    location ~* ^/(agent|ssh)/?\$ {
        if (\$ssl_client_verify != SUCCESS) { return 403; }
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header X-Client-Verify \$ssl_client_verify;
        proxy_set_header X-Client-Dn \$ssl_client_s_dn;
        proxy_set_header X-Client-Fingerprint \$ssl_client_fingerprint;   # the server compares it with the certificate it last issued
        proxy_set_header X-RAC-Proxy "${PROXY_SECRET}";
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection \$connection_upgrade;
        proxy_read_timeout 3600s;
    }
    location ~* ^/api/ {
        if (\$ssl_client_verify != SUCCESS) { return 403; }
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Client-Verify \$ssl_client_verify;
        # The DN is how the server knows WHICH device is talking; without it /api/telemetry and
        # /api/vnc-secret answer 401.
        proxy_set_header X-Client-Dn \$ssl_client_s_dn;
        proxy_set_header X-Client-Fingerprint \$ssl_client_fingerprint;   # the server compares it with the certificate it last issued
        proxy_set_header X-RAC-Proxy "${PROXY_SECRET}";
    }
    location ~* ^/admin/ {
        allow 127.0.0.1; deny all;
        proxy_pass http://127.0.0.1:5000;
        proxy_set_header Host \$host;
        proxy_set_header X-Client-Verify "";
        proxy_set_header X-Client-Dn "";
        proxy_set_header X-Client-Fingerprint "";
    }
    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        # A client's own identity headers never pass: only the certificate-checked locations above set them.
        proxy_set_header X-Client-Verify "";
        proxy_set_header X-Client-Dn "";
        proxy_set_header X-Client-Fingerprint "";
        proxy_set_header X-RAC-Proxy "${PROXY_SECRET}";   # vouches for X-Real-IP (sign-in locks, audit)
    }
}
NGINX
sudo chmod 600 "/etc/nginx/sites-available/${RAC_DOMAIN}"   # it holds the proxy secret
sudo ln -sf "/etc/nginx/sites-available/${RAC_DOMAIN}" "/etc/nginx/sites-enabled/${RAC_DOMAIN}"
sudo rm -f /etc/nginx/sites-enabled/default

# 443 stream multiplexer (static config; no per-domain values - quoted heredoc).
sudo tee /etc/nginx/stream-rac.conf >/dev/null <<'STREAM'
# 443 multiplexer (replaces sslh): SSH + HTTPS share the port; real client IP via PROXY protocol.
stream {
    map $ssl_preread_protocol $rac_upstream {
        ""      127.0.0.1:2222;   # not TLS (SSH) -> strip-relay -> sshd
        default 127.0.0.1:8443;   # TLS (HTTPS/WSS) -> nginx http (proxy_protocol)
    }
    server {
        listen 443;
        ssl_preread on;
        proxy_pass $rac_upstream;
        proxy_protocol on;
    }
    server {
        listen 127.0.0.1:2222 proxy_protocol;
        proxy_pass 127.0.0.1:22;
    }
}
STREAM

# include the stream block at the TOP level of nginx.conf (idempotent).
grep -q 'stream-rac.conf' /etc/nginx/nginx.conf || echo 'include /etc/nginx/stream-rac.conf;' | sudo tee -a /etc/nginx/nginx.conf >/dev/null

sudo mkdir -p /etc/letsencrypt/renewal-hooks/deploy
printf '#!/bin/sh\nsystemctl reload nginx\n' | sudo tee /etc/letsencrypt/renewal-hooks/deploy/reload-nginx.sh >/dev/null
sudo chmod +x /etc/letsencrypt/renewal-hooks/deploy/reload-nginx.sh

sudo nginx -t
sudo systemctl restart nginx
# The server reads the secret only at start; nginx sends it from now on, so the two match after this.
sudo systemctl restart remoteserver.service
ok "nginx 443 mux configured (HTTPS/WSS + SSH on 443, real IP preserved)"
