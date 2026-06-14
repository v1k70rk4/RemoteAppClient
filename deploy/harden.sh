#!/usr/bin/env bash
# Firewall (ufw), fail2ban, and sshd hardening. Idempotent.
# Order matters: allow SSH before enabling the firewall so you do not lock yourself out.
set -euo pipefail

# --- ufw: only 443 + SSH from outside ---
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq ufw
sudo ufw allow OpenSSH
sudo ufw allow 443/tcp
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw --force enable
sudo ufw status verbose

# --- fail2ban against SSH brute force ---
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

# --- sshd: root login off, password auth off (keys only) ---
sudo tee /etc/ssh/sshd_config.d/99-hardening.conf >/dev/null <<'SSHD'
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
SSHD
sudo sshd -t
sudo systemctl reload ssh
echo "[harden] done."
