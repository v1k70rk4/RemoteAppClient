#!/usr/bin/env bash
# Tűzfal (ufw) + fail2ban + sshd-hardening. Idempotens.
# Sorrend: előbb az SSH engedélyezése, csak utána a tűzfal bekapcsolása (ne zárjuk ki magunkat).
set -euo pipefail

# --- ufw: csak 443 + SSH kintről ---
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq ufw
sudo ufw allow OpenSSH
sudo ufw allow 443/tcp
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw --force enable
sudo ufw status verbose

# --- fail2ban a brute-force ellen (SSH) ---
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq fail2ban
sudo tee /etc/fail2ban/jail.local >/dev/null <<'JAIL'
[sshd]
enabled = true
backend = systemd
maxretry = 5
bantime = 1h
JAIL
sudo systemctl enable --now fail2ban
sudo systemctl restart fail2ban

# --- sshd: root-login ki, jelszavas auth ki (csak kulcs) ---
sudo tee /etc/ssh/sshd_config.d/99-hardening.conf >/dev/null <<'SSHD'
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
SSHD
sudo sshd -t
sudo systemctl reload ssh
echo "[harden] kész."
