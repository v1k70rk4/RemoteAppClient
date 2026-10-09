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

# surface the first console login right next to the blob: the server writes it to an owner-only file when it
# seeds the DB (Server:FirstAdminPasswordPath) and removes the file once that password has been changed.
cred_file="/var/lib/remoteserver/first-admin-password.txt"
if sudo test -f "$cred_file"; then
  echo "   first console login -> $(sudo grep -F 'password:' "$cred_file" | sed 's/^temporary password: //' | sed 's/^/admin \/ /')"
  info "the same is in $cred_file (service user only); it disappears after the first password change"
else
  info "first admin password: sudo cat $cred_file (already changed if the file is gone)"
fi
echo "------------------------------------------------------------------"
info "copy the blob above; on the first Windows device: RemoteAgent.exe bootstrap \"<blob>\""
info "then sign in to the client (pointed at this server) with the admin login above."
