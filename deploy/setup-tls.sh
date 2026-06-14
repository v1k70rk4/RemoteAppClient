#!/usr/bin/env bash
# Let's Encrypt certificate via DNS-01 challenge (Cloudflare API).
# No open port 80 is needed. Auto-renewal is handled by the certbot systemd timer.
# Prerequisite: /etc/letsencrypt/cloudflare.ini with a scoped Cloudflare token (chmod 600).
#
# Usage: DOMAIN=racd.example.com ACME_EMAIL=admin@example.com ./setup-tls.sh
set -euo pipefail

DOMAIN="${DOMAIN:?Set DOMAIN, for example DOMAIN=racd.example.com}"
ACME_EMAIL="${ACME_EMAIL:?Set ACME_EMAIL, for example ACME_EMAIL=admin@example.com}"
CF_INI="/etc/letsencrypt/cloudflare.ini"

sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq certbot python3-certbot-dns-cloudflare

if ! sudo test -f "$CF_INI"; then
  echo "ERROR: missing $CF_INI (the Cloudflare token)." >&2
  exit 1
fi
sudo chmod 600 "$CF_INI"

sudo certbot certonly \
  --dns-cloudflare \
  --dns-cloudflare-credentials "$CF_INI" \
  --dns-cloudflare-propagation-seconds 30 \
  -d "$DOMAIN" \
  --key-type ecdsa \
  --non-interactive --agree-tos -m "$ACME_EMAIL"

echo "=== certificate files ==="
sudo ls -l "/etc/letsencrypt/live/${DOMAIN}/"
