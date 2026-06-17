# Step 08 - firewall + fail2ban + sshd hardening. DISABLES password SSH login.
require_sudo
if ! ask_yn "Key-based SSH confirmed working for your admin user? (this disables password login)" "n"; then
  warn "skipping hardening - verify key login, then run: ./setup.sh 08-harden"
  return 0
fi

sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq ufw fail2ban
sudo ufw allow OpenSSH
sudo ufw allow 443/tcp
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw --force enable

sudo tee /etc/fail2ban/jail.local >/dev/null <<'JAIL'
[sshd]
enabled = true
backend = systemd
maxretry = 5
bantime = 1h
JAIL
sudo systemctl enable --now fail2ban
sudo systemctl restart fail2ban

sudo tee /etc/ssh/sshd_config.d/99-hardening.conf >/dev/null <<'SSHD'
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
SSHD
sudo sshd -t
sudo systemctl reload ssh
ok "hardening applied (ufw 443+SSH, fail2ban, sshd keys-only)"
