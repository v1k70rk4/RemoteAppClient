#!/usr/bin/env bash
# Bástya: lebutított 'agent' SSH-user a reverse tunnelekhez, SSH-CA alapon.
# Az agentek a CA által aláírt SSH-certtel jönnek — nincs authorized_keys-kezelés.
# A forwardolt portok GatewayPorts=no miatt CSAK a bástya localhostján látszanak.
# Idempotens.
set -euo pipefail

AGENT_USER="agent"
CA_DIR="/etc/remoteserver"
CA_KEY="$CA_DIR/agent_ca"
SSHD_CA_PUB="/etc/ssh/agent_ca.pub"
SVC_USER="remotesrv"

# 1) lebutított agent user (nincs shell, nincs jelszó)
if ! id "$AGENT_USER" &>/dev/null; then
  sudo useradd --system --create-home --shell /usr/sbin/nologin "$AGENT_USER"
  echo "[bastion] '$AGENT_USER' user létrehozva."
fi

# 2) SSH-CA kulcspár — a szerver-app (remotesrv) ezzel írja alá a cert-eket
if ! sudo test -f "$CA_KEY"; then
  sudo ssh-keygen -t ed25519 -f "$CA_KEY" -N "" -C "RemoteAppClient agent CA" -q
  echo "[bastion] SSH-CA generálva."
fi
sudo chown "$SVC_USER:$SVC_USER" "$CA_KEY" "$CA_KEY.pub"
sudo chmod 600 "$CA_KEY"

# a publikus CA-t az sshd olvassa (root-owned)
sudo cp "$CA_KEY.pub" "$SSHD_CA_PUB"
sudo chown root:root "$SSHD_CA_PUB"
sudo chmod 644 "$SSHD_CA_PUB"

# 3) sshd Match blokk az agent userre.
#    -R (reverse tunnel, cél gép VNC-je) ÉS -L (konzol-bróker: admin API + cél-port)
#    is kell. A -L célja a bástya LOOPBACKja (PermitOpen 127.0.0.1:*) — kifelé nem mehet.
#    A loopback szolgáltatások jelszóval védettek (mariadb), a tunnel-portok VNC-jelszóval
#    + szerver-oldali grant-ellenőrzéssel, így a loopback-* elfogadható kockázat.
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
echo "[bastion] sshd Match aktív."

# 4) a bástya host-kulcsa (az agent ezt pinneli a known_hosts-ban: 'típus kulcs')
echo "[bastion] >>> BASTION HOST KEY (az agent BastionHostKey-jébe, comment nélkül):"
sudo awk '{print $1, $2}' /etc/ssh/ssh_host_ed25519_key.pub
