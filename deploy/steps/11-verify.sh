# Step 11 - verify that everything that should be there is there.
fail=0
chk() { if eval "$2" >/dev/null 2>&1; then ok "$1"; else warn "$1 - MISSING/FAILED"; fail=$((fail+1)); fi; }

chk "remoteserver service active"  'sudo systemctl is-active --quiet remoteserver.service'
chk "health endpoint responds"     'curl -fsS --max-time 5 http://127.0.0.1:5000/health'
chk "db.env present"               'sudo test -f "$RAC_ENV_DIR/db.env"'
chk "command-signing key"          'sudo test -f "$RAC_ENV_DIR/cmd_signing.key"'
chk "client CA (ca.crt)"           'sudo test -f "$RAC_ENV_DIR/ca.crt"'
chk "secret.key"                   'sudo test -f "$RAC_ENV_DIR/secret.key"'
chk "SSH agent CA"                 'sudo test -f "$RAC_ENV_DIR/agent_ca"'
chk "PublicUrl configured"         'sudo grep -q "Server__PublicUrl=" "$RAC_ENV_DIR/bastion.env"'
chk "bastion host key in env"      'sudo grep -q "Server__Bastion__HostKey=" "$RAC_ENV_DIR/bastion.env"'
chk "agent user exists"            'id "$RAC_AGENT_USER"'
chk "sshd agent Match block"       'sudo test -f /etc/ssh/sshd_config.d/agent-bastion.conf'
chk "TLS certificate present"      'sudo test -f "/etc/letsencrypt/live/${RAC_DOMAIN}/fullchain.pem"'
chk "nginx config valid"          'sudo nginx -t'
chk "stream mux included"          'sudo grep -q "stream-rac.conf" /etc/nginx/nginx.conf'
chk "443 (mux) listening"          'sudo ss -ltn | grep -q ":443 "'
chk "8443 (http vhost) listening"  'sudo ss -ltn | grep -q "127.0.0.1:8443 "'
chk "self-update path enabled"     'systemctl is-enabled --quiet remoteserver-update.path'
chk "rollback path enabled"        'systemctl is-enabled --quiet remoteserver-rollback.path'
chk "sshd config valid"            'sudo sshd -t'

if [ "$fail" -eq 0 ]; then
  ok "ALL CHECKS PASSED - server is ready; grab the blob from step 09 and enroll a device."
else
  warn "$fail check(s) failed - re-run the matching step, e.g. ./setup.sh 06-tls"
fi
