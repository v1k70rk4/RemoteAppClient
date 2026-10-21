# Step 05 - bastion: restricted 'agent' SSH user + SSH CA + sshd Match block for reverse tunnels.
require_sudo
id "$RAC_AGENT_USER" &>/dev/null || sudo useradd --system --create-home --shell /usr/sbin/nologin "$RAC_AGENT_USER"

CA_KEY="$RAC_ENV_DIR/agent_ca"
sudo test -f "$CA_KEY" || sudo ssh-keygen -t ed25519 -f "$CA_KEY" -N "" -C "RemoteAppClient agent CA" -q
sudo chown "$RAC_SVC_USER:$RAC_SVC_USER" "$CA_KEY" "$CA_KEY.pub"; sudo chmod 600 "$CA_KEY"
sudo cp "$CA_KEY.pub" /etc/ssh/agent_ca.pub
sudo chown root:root /etc/ssh/agent_ca.pub; sudo chmod 644 /etc/ssh/agent_ca.pub

# Deleted devices' SSH keys: the server keeps this KRL up to date (Signing/SshRevocationList), and every
# certificate for a listed key is refused. An unreadable RevokedKeys file refuses ALL keys of the users it
# applies to, so it is created here (empty) before sshd is told about it - and it sits inside the Match block,
# so even then only the agent user would be affected, never an administrator's own login.
KRL=/var/lib/remoteserver/ssh/revoked_keys.krl
sudo mkdir -p "$(dirname "$KRL")"
sudo test -s "$KRL" || sudo ssh-keygen -k -f "$KRL"
sudo touch "${KRL%.krl}.txt"
sudo chown -R "$RAC_SVC_USER:$RAC_SVC_USER" "$(dirname "$KRL")"
sudo chmod 755 "$(dirname "$KRL")"; sudo chmod 644 "$KRL" "${KRL%.krl}.txt"

# Who may bring a reverse tunnel up, and on which ports. Every certificate carries the same principal, so
# without this any valid certificate - every enrolled machine's - could bind another device's reverse port.
# sshd asks rac-principals (below) for the certificate's key id. The server writes tunnel-grants.txt the moment
# it delivers an open-tunnel command (device id, VNC port, file port, time); a device named there within the
# last two minutes may listen on those two ports and nothing else, any other device and every operator
# certificate ("operator:<user>") may not listen at all. -L forwards (the console reaching the admin API and
# the devices' ports through its own agent) stay open to everyone. The command runs as nobody and must be
# root-owned and not group/other-writable, or sshd refuses it.
GRANTS="$(dirname "$KRL")/tunnel-grants.txt"
sudo tee /usr/local/sbin/rac-principals >/dev/null <<'SCRIPT'
#!/bin/sh
# AuthorizedPrincipalsCommand for the bastion's agent user: prints the principal line, with listen rights,
# for the certificate whose key id is $1. See deploy/steps/05-bastion.sh.
KEYID="$1"
GRANTS="@GRANTS@"
ENFORCE="@ENFORCE@"
PRINCIPAL="@AGENT_USER@"
WINDOW=120
OPEN='permitopen="127.0.0.1:*"'          # -L to the admin API and to devices' loopback ports (the console model)
NOLISTEN='permitlisten="localhost:1"'    # port 1 is unusable, so this is "no listen" (sshd knows no "none" here)
log() { command -v logger >/dev/null 2>&1 && logger -t rac-principals "$*"; }

# Observe mode until the enforce file exists: everyone unrestricted, decisions only logged. A non-empty
# enforce file names the key ids to restrict (a canary), an empty one restricts everyone.
if [ ! -f "$ENFORCE" ]; then
    log "observe: $KEYID unrestricted (no $ENFORCE)"
    echo "$OPEN $PRINCIPAL"; exit 0
fi
if [ -s "$ENFORCE" ] && ! grep -qxF -- "$KEYID" "$ENFORCE"; then
    log "canary: $KEYID unrestricted (not listed in $ENFORCE)"
    echo "$OPEN $PRINCIPAL"; exit 0
fi

case "$KEYID" in
    operator:*)
        log "operator $KEYID: no listen"
        echo "$OPEN,$NOLISTEN $PRINCIPAL"; exit 0;;
esac

NOW=$(date +%s)
LINE=$(awk -F'\t' -v id="$KEYID" '$1 == id { print $2, $3, $4; exit }' "$GRANTS" 2>/dev/null)
if [ -z "$LINE" ]; then
    log "$KEYID: no open-tunnel requested, no listen"
    echo "$OPEN,$NOLISTEN $PRINCIPAL"; exit 0
fi
set -- $LINE
VNC="$1"; FILE="$2"; AT="$3"
if [ $((NOW - AT)) -gt "$WINDOW" ] || [ $((NOW - AT)) -lt -60 ]; then
    log "$KEYID: request is $((NOW - AT))s old, no listen"
    echo "$OPEN,$NOLISTEN $PRINCIPAL"; exit 0
fi
# ssh sends the name "localhost" for a -R without a bind address, which sshd matches separately from the
# loopback addresses; all three forms are listed so the agent's forward is accepted whichever it sends.
L=""
for p in "$VNC" "$FILE"; do
    [ "$p" -gt 0 ] 2>/dev/null || continue   # a file session is granted the file port alone (VNC port 0)
    L="$L,permitlisten=\"localhost:$p\",permitlisten=\"127.0.0.1:$p\",permitlisten=\"[::1]:$p\""
done
[ -n "$L" ] || L=",$NOLISTEN"
log "$KEYID: listen on $VNC and $FILE (requested $((NOW - AT))s ago)"
echo "$OPEN$L $PRINCIPAL"
SCRIPT
sudo sed -i -e "s|@GRANTS@|${GRANTS}|" -e "s|@ENFORCE@|${GRANTS%.txt}-enforce|" -e "s|@AGENT_USER@|${RAC_AGENT_USER}|" /usr/local/sbin/rac-principals
sudo chown root:root /usr/local/sbin/rac-principals; sudo chmod 755 /usr/local/sbin/rac-principals
sudo test -f "$GRANTS" || { sudo touch "$GRANTS"; sudo chown "$RAC_SVC_USER:$RAC_SVC_USER" "$GRANTS"; sudo chmod 644 "$GRANTS"; }

sudo tee /etc/ssh/sshd_config.d/agent-bastion.conf >/dev/null <<CONF
Match User ${RAC_AGENT_USER}
    TrustedUserCAKeys /etc/ssh/agent_ca.pub
    RevokedKeys ${KRL}
    AuthorizedPrincipalsCommand /usr/local/sbin/rac-principals %i
    AuthorizedPrincipalsCommandUser nobody
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
ok "bastion ready (agent user + SSH CA + sshd Match block + per-request listen rights + reverse-tunnel keepalive)"
info "listen rights are in OBSERVE mode (decisions logged under 'rac-principals' in the journal, nobody restricted)."
info "To enforce: test one device first -  echo <deviceId> | sudo tee ${GRANTS%.txt}-enforce  - open its VNC and files;"
info "then enforce every device:          sudo truncate -s0 ${GRANTS%.txt}-enforce"
info ">>> bastion host key (already in bastion.env; shown for reference):"
sudo awk '{print $1, $2}' /etc/ssh/ssh_host_ed25519_key.pub
