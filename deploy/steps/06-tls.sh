# Step 06 - TLS certificate via Let's Encrypt (Cloudflare DNS-01). Skips if one already exists.
require_sudo
if sudo test -f "/etc/letsencrypt/live/${RAC_DOMAIN}/fullchain.pem"; then
  ok "certificate already present for ${RAC_DOMAIN}"
  return 0
fi
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq certbot python3-certbot-dns-cloudflare

CF_INI="/etc/letsencrypt/cloudflare.ini"
if ! sudo test -f "$CF_INI"; then
  warn "Cloudflare token file $CF_INI is missing."
  if ask_yn "Enter a Cloudflare API token now (Zone:DNS:Edit)?" "y"; then
    tok="$(ask_secret 'Cloudflare API token')"
    printf 'dns_cloudflare_api_token = %s\n' "$tok" | sudo tee "$CF_INI" >/dev/null
  else
    die "no Cloudflare token. Put it at $CF_INI (or issue the cert another way), then re-run: ./setup.sh 06-tls"
  fi
fi
sudo chmod 600 "$CF_INI"

sudo certbot certonly --dns-cloudflare --dns-cloudflare-credentials "$CF_INI" \
  --dns-cloudflare-propagation-seconds 30 -d "$RAC_DOMAIN" --key-type ecdsa \
  --non-interactive --agree-tos -m "$RAC_ACME_EMAIL"
ok "certificate issued for ${RAC_DOMAIN}"
