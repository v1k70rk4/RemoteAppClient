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

# Release a dropped agent's reverse-forward (-R) port quickly. A device's -R port is deterministic,
# so while the bastion still holds it from a session that has not timed out, the reconnecting agent's
# bind is refused (its ssh runs ExitOnForwardFailure) and VNC stays dead until the port frees. 15x3
# mirrors the agent's own ssh keepalive (ServerAliveInterval=15/CountMax=3), so both ends give the
# link up at the same moment (~45s) instead of the bastion lagging minutes behind.
# The 10- prefix is load-bearing: Include sits near the top of sshd_config and the FIRST obtained
# value wins, so this must sort before the cloud image's 50-cloudimg-settings.conf (ClientAliveInterval 120).
sudo tee /etc/ssh/sshd_config.d/10-keepalive.conf >/dev/null <<'CONF'
ClientAliveInterval 15
ClientAliveCountMax 3
CONF

sudo sshd -t
sudo systemctl reload ssh
ok "bastion ready (agent user + SSH CA + sshd Match block + reverse-tunnel keepalive)"
info ">>> bastion host key (already in bastion.env; shown for reference):"
sudo awk '{print $1, $2}' /etc/ssh/ssh_host_ed25519_key.pub
