#!/usr/bin/env bash
# Let's Encrypt cert DNS-01 challenge-dzsel (Cloudflare API).
# Nem kell hozzá nyitott 80-as port. Auto-renew: a certbot systemd-timere intézi.
# Előfeltétel: /etc/letsencrypt/cloudflare.ini a scoped Cloudflare tokennel (chmod 600).
#
# Használat:  DOMAIN=racd.example.com ACME_EMAIL=admin@example.com ./setup-tls.sh
set -euo pipefail

DOMAIN="${DOMAIN:?Állítsd be a DOMAIN-t, pl. DOMAIN=racd.example.com}"
ACME_EMAIL="${ACME_EMAIL:?Állítsd be az ACME_EMAIL-t, pl. ACME_EMAIL=admin@example.com}"
CF_INI="/etc/letsencrypt/cloudflare.ini"

sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq certbot python3-certbot-dns-cloudflare

if ! sudo test -f "$CF_INI"; then
  echo "HIBA: hiányzik $CF_INI (a Cloudflare token)." >&2
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

echo "=== cert fájlok ==="
sudo ls -l "/etc/letsencrypt/live/${DOMAIN}/"
