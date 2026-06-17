# Step 05 - bastion: restricted 'agent' SSH user + SSH CA + sshd Match block for reverse tunnels.
require_sudo
id "$RAC_AGENT_USER" &>/dev/null || sudo useradd --system --create-home --shell /usr/sbin/nologin "$RAC_AGENT_USER"

CA_KEY="$RAC_ENV_DIR/agent_ca"
sudo test -f "$CA_KEY" || sudo ssh-keygen -t ed25519 -f "$CA_KEY" -N "" -C "RemoteAppClient agent CA" -q
sudo chown "$RAC_SVC_USER:$RAC_SVC_USER" "$CA_KEY" "$CA_KEY.pub"; sudo chmod 600 "$CA_KEY"
sudo cp "$CA_KEY.pub" /etc/ssh/agent_ca.pub
sudo chown root:root /etc/ssh/agent_ca.pub; sudo chmod 644 /etc/ssh/agent_ca.pub

sudo tee /etc/ssh/sshd_config.d/agent-bastion.conf >/dev/null <<CONF
Match User ${RAC_AGENT_USER}
    TrustedUserCAKeys /etc/ssh/agent_ca.pub
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
ok "bastion ready (agent user + SSH CA + sshd Match block)"
info ">>> bastion host key (already in bastion.env; shown for reference):"
sudo awk '{print $1, $2}' /etc/ssh/ssh_host_ed25519_key.pub
