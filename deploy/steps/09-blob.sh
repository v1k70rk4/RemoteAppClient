# Step 09 - mint the first bootstrap blob (devices enroll as Pending; an admin approves them).
# mint-blob first checks schema, first admin, signing key, CA, PublicUrl, bastion config, secret key.
require_sudo
info "minting bootstrap blob (this also validates the install)..."
echo "------------------------------------------------------------------"
sudo systemd-run --quiet --wait --collect --pipe --uid="$RAC_SVC_USER" --working-directory="$RAC_APP_DIR" \
  --property=EnvironmentFile="$RAC_ENV_DIR/db.env" \
  --property=EnvironmentFile=-"$RAC_ENV_DIR/bastion.env" \
  "$RAC_APP_DIR/RemoteServer" mint-blob
echo "------------------------------------------------------------------"

# surface the first console login right next to the blob (the server logs it when it seeds the DB)
cred="$(sudo journalctl -u remoteserver --no-pager 2>/dev/null \
        | grep -F 'BOOTSTRAP admin created' | tail -1 \
        | sed 's/.*BOOTSTRAP admin created - /first console login -> /')"
if [ -n "$cred" ]; then
  echo "   $cred"
else
  info "first admin password: sudo journalctl -u remoteserver | grep 'BOOTSTRAP admin'"
fi
echo "------------------------------------------------------------------"
info "copy the blob above; on the first Windows device: RemoteAgent.exe bootstrap \"<blob>\""
info "then sign in to the client (pointed at this server) with the admin login above."
