#!/usr/bin/env bash
# Bastion: restricted 'agent' SSH user for reverse tunnels, based on SSH CA certs.
# Agents arrive with SSH certificates signed by the CA; there is no authorized_keys management.
# Forwarded ports are visible only on bastion localhost because GatewayPorts=no.
# Idempotent.
set -euo pipefail

AGENT_USER="agent"
CA_DIR="/etc/remoteserver"
CA_KEY="$CA_DIR/agent_ca"
SSHD_CA_PUB="/etc/ssh/agent_ca.pub"
SVC_USER="remotesrv"

# 1) restricted agent user (no shell, no password)
if ! id "$AGENT_USER" &>/dev/null; then
  sudo useradd --system --create-home --shell /usr/sbin/nologin "$AGENT_USER"
  echo "[bastion] '$AGENT_USER' user created."
fi

# 2) SSH CA key pair. The server app (remotesrv) uses this to sign certs.
if ! sudo test -f "$CA_KEY"; then
  sudo ssh-keygen -t ed25519 -f "$CA_KEY" -N "" -C "RemoteAppClient agent CA" -q
  echo "[bastion] SSH CA generated."
fi
sudo chown "$SVC_USER:$SVC_USER" "$CA_KEY" "$CA_KEY.pub"
sudo chmod 600 "$CA_KEY"

# sshd reads the public CA key (root-owned)
sudo cp "$CA_KEY.pub" "$SSHD_CA_PUB"
sudo chown root:root "$SSHD_CA_PUB"
sudo chmod 644 "$SSHD_CA_PUB"

# 3) sshd Match block for the agent user.
#    Both -R (reverse tunnel to the target device's VNC) and -L (console broker:
#    admin API + target port) are needed. -L targets bastion LOOPBACK
#    (PermitOpen 127.0.0.1:*), so it cannot reach outward.
#    Loopback services are password-protected (MariaDB), and tunnel ports are
#    protected by VNC passwords plus server-side grant checks, so loopback-* is
#    an acceptable scoped risk.
sudo tee /etc/ssh/sshd_config.d/agent-bastion.conf >/dev/null <<CONF
Match User ${AGENT_USER}
    TrustedUserCAKeys ${SSHD_CA_PUB}
    GatewayPorts no
    AllowTcpForwarding all
    PermitTTY no
    X11Forwarding no
    AllowAgentForwarding no
    PermitOpen 127.0.0.1:*
    ForceCommand /usr/sbin/nologin
CONF
sudo sshd -t
sudo systemctl reload ssh
echo "[bastion] sshd Match block active."

# 4) bastion host key. The agent pins this in known_hosts: "type key".
echo "[bastion] >>> BASTION HOST KEY (put into agent BastionHostKey, without comment):"
sudo awk '{print $1, $2}' /etc/ssh/ssh_host_ed25519_key.pub
