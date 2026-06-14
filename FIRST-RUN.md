# First Run

This guide takes a fresh server to the first enrolled Windows device. It follows
the scripts in [deploy/](deploy/) and assumes the server will be published through
nginx with TLS, while the admin API remains reachable only through the device
tunnel.

## What You Need

- A Linux server with sudo access.
- A public DNS name, for example `remote.example.com`.
- MariaDB 10.11 or newer.
- The repository files, at least `deploy/` and `src/RemoteServer/Data/Migrations/schema.sql`.
- A `RemoteServer` Linux package copied to `/tmp/remoteserver.tar.gz`.
- Windows release artifacts for the first device:
  `RemoteAgent.exe`, `RemoteAgent.Updater.exe`, and optionally `RemoteClient.exe`.
- Optional but recommended: the pinned TightVNC MSI downloaded by
  `deploy/fetch-tightvnc.sh`.

The examples below use these variables:

```bash
export DOMAIN=remote.example.com
export ACME_EMAIL=admin@example.com
export BASTION_HOST=$DOMAIN
```

Replace them with your real domain and email address.

## Security Shape

`RemoteServer` listens on `127.0.0.1:5000` behind nginx. Do not expose that port
directly.

nginx is expected to expose:

| Path | Public exposure | Purpose |
| --- | --- | --- |
| `/` | Public | Health/start page. |
| `/auth/*` | Public through nginx | Sign-in and first account setup endpoints. |
| `/agent` | Public, mTLS required | Agent WebSocket command channel. |
| `/api/*` | Public, mTLS required | Agent telemetry, package download, VNC secret report. |
| `/admin/*` | Localhost only | Console/admin API, reached through the device tunnel. |

The first bootstrap blob is created on the server because the console normally
needs an enrolled device tunnel before it can reach `/admin/*`.

## 1. Build Or Copy The Server Package

The deployment script expects the server package here:

```bash
/tmp/remoteserver.tar.gz
```

You can use the release artifact `RemoteServer-linux-x64.tar.gz` and rename it,
or build it yourself:

```bash
dotnet publish src/RemoteServer/RemoteServer.csproj -c Release -r linux-x64 --self-contained -o out
tar -C out -czf /tmp/remoteserver.tar.gz .
```

Copy the package, `deploy/`, and `src/RemoteServer/Data/Migrations/schema.sql`
to the server before continuing.

## 2. Create The Database

Run the database setup script:

```bash
deploy/setup-db.sh
```

It creates the `remoteserver` database and user, generates a local password, and
writes the connection string to:

```bash
/etc/remoteserver/db.env
```

Load the baseline schema:

```bash
sudo mariadb remoteserver < src/RemoteServer/Data/Migrations/schema.sql
```

## 3. Deploy RemoteServer

Install the self-contained server, generate local secrets, and create the systemd
unit:

```bash
BASTION_HOST="$BASTION_HOST" deploy/deploy-server.sh
```

The script creates:

| File or directory | Purpose |
| --- | --- |
| `/opt/remoteserver` | Installed server files. |
| `/etc/remoteserver/cmd_signing.key` | Server command-signing private key. |
| `/etc/remoteserver/ca.key` and `/etc/remoteserver/ca.crt` | Client certificate authority for agent mTLS. |
| `/etc/remoteserver/secret.key` | Local encryption key for protected database fields. |
| `/etc/remoteserver/bastion.env` | Bastion host settings loaded by systemd. |
| `/var/lib/remoteserver/packages` | Release package storage. |
| `remoteserver.service` | systemd service running as `remotesrv`. |

The command-signing public key and CA fingerprint printed by the script are
useful for checking the install. Enrollment responses also include the values
agents need, so you normally do not paste them into agent config by hand.

## 4. Set The Public URL

Bootstrap blobs and MSI builds need the public base URL.

If `Server__PublicUrl` is not already present in
`/etc/remoteserver/bastion.env`, add it:

```bash
echo "Server__PublicUrl=https://${DOMAIN}" | sudo tee -a /etc/remoteserver/bastion.env
sudo systemctl restart remoteserver
```

If you rerun this later, edit the existing line instead of adding a duplicate.

## 5. Configure The Bastion

Set up the restricted SSH user and SSH certificate authority used by device
tunnels:

```bash
deploy/setup-bastion.sh
```

This creates the `agent` user, configures sshd with
`TrustedUserCAKeys`, restricts forwarding, and keeps reverse forwards bound to
the bastion localhost.

If the bastion and server are on the same host, `deploy-server.sh` usually writes
the host key into `/etc/remoteserver/bastion.env`. If the bastion is separate,
copy the host key printed by `setup-bastion.sh` into that file:

```bash
Server__Bastion__HostKey=ssh-ed25519 AAAA...
```

Then restart the server:

```bash
sudo systemctl restart remoteserver
```

## 6. Issue TLS Certificates

The bundled script uses Let's Encrypt with Cloudflare DNS-01 validation. Create a
root-only Cloudflare credentials file first:

```bash
sudo install -m 600 -o root -g root /path/to/cloudflare.ini /etc/letsencrypt/cloudflare.ini
```

Then request the certificate:

```bash
DOMAIN="$DOMAIN" ACME_EMAIL="$ACME_EMAIL" deploy/setup-tls.sh
```

If you do not use Cloudflare, issue the certificate with your own ACME flow and
place it where the nginx configuration expects the Let's Encrypt certificate for
the same domain.

## 7. Configure nginx

Install the reverse proxy configuration:

```bash
DOMAIN="$DOMAIN" deploy/setup-nginx.sh
```

Check nginx and the public endpoint:

```bash
sudo nginx -t
curl -I "https://${DOMAIN}/"
```

`/admin/*` should not be reachable from the public internet. It is intentionally
localhost-only and is used by the console through a device tunnel.

## 8. Harden The Server

Before running this, make sure key-based SSH login works for your own admin user.
The hardening script disables password SSH login and root SSH login.

```bash
deploy/harden.sh
```

It enables UFW, allows SSH and HTTPS, installs fail2ban, and writes sshd
hardening configuration.

## 9. Get The First Admin Password

On first startup the server seeds the `admin` account with a temporary password.
Read it from the journal:

```bash
sudo journalctl -u remoteserver -n 300 --no-pager | grep BOOTSTRAP
```

The first sign-in requires a password change and TOTP setup.

## 10. Create The First Bootstrap Blob

Run `mint-blob` with the same environment files as the service:

```bash
sudo systemd-run --quiet --wait --collect --pipe --uid=remotesrv --working-directory=/opt/remoteserver \
  --property=EnvironmentFile=/etc/remoteserver/db.env \
  --property=EnvironmentFile=-/etc/remoteserver/bastion.env \
  /opt/remoteserver/RemoteServer mint-blob
```

The helper checks the database schema, first admin, command-signing key, client
CA, `Server__PublicUrl`, bastion config, and secret key. It prints the bootstrap
blob only when the required pieces are present.

The generated token uses `autoApprove=false`. Devices enrolled with it appear as
`Pending` and must be approved in the console.

## 11. Prepare The First Windows Device

For the first machine, manual enrollment is the simplest path. Later, use the
console to upload packages and build group-specific MSI installers.

On a Windows device, open an elevated PowerShell session and copy the release
artifacts into one directory:

```powershell
New-Item -ItemType Directory -Force 'C:\Program Files\RemoteAppClient'
Copy-Item .\RemoteAgent.exe,.\RemoteAgent.Updater.exe,.\RemoteClient.exe 'C:\Program Files\RemoteAppClient'
Set-Location 'C:\Program Files\RemoteAppClient'
```

If you want TightVNC provisioned on first start, place the pinned MSI under the
agent's `vnc` directory:

```powershell
New-Item -ItemType Directory -Force '.\vnc'
Copy-Item .\tightvnc.msi '.\vnc\tightvnc.msi'
```

Write the bootstrap blob and install the services:

```powershell
.\RemoteAgent.exe bootstrap "<blob>"
.\RemoteAgent.exe install-service --owner "Company Name" --group "First devices"
```

The agent enrolls on first service start, receives its client certificate and
bastion settings, opens its tunnel, and reports telemetry. Because the bootstrap
token is not auto-approved, the device stays `Pending` until an admin approves it.

## 12. Open The Console And Approve The Device

On the enrolled Windows device, start:

```powershell
.\RemoteClient.exe
```

Sign in as `admin` with the temporary password from the server journal. Complete
the forced password change and TOTP setup.

Then approve the first device in the Devices view. After approval, the normal
remote access workflow can be tested from the same console.

## 13. Upload Packages And Build MSI Installers

Once the console can reach the admin API through the tunnel:

1. Upload release packages for the desired channel:
   `agent`, `updater`, `client`, and optionally `vnc`.
2. Use the Channels MSI builder to create a group-specific installer.
3. Install that MSI on additional Windows devices.

The MSI embeds a group bootstrap token, installs the agent and updater services,
optionally places the client shortcut, and can bundle TightVNC under
`INSTALLDIR\vnc\tightvnc.msi`.

To fetch the pinned TightVNC binary and matching GPL source archive:

```bash
deploy/fetch-tightvnc.sh
```

It writes:

```text
third_party/tightvnc/tightvnc.msi
third_party/tightvnc/tightvnc-src.zip
```

## Troubleshooting

Server checks:

```bash
sudo systemctl status remoteserver
sudo journalctl -u remoteserver -f
sudo nginx -t
sudo tail -f /var/log/nginx/error.log
```

Windows checks:

```powershell
Get-Service RemoteAgent,RemoteAgent.Updater
Get-ChildItem 'C:\ProgramData\RemoteAgent'
Get-WinEvent -LogName Application -MaxEvents 100 | Where-Object ProviderName -eq 'RemoteAgent'
```

Common symptoms:

| Symptom | Check |
| --- | --- |
| `mint-blob` reports missing `PublicUrl` | Set `Server__PublicUrl` in `/etc/remoteserver/bastion.env` and restart `remoteserver`. |
| `mint-blob` reports missing schema | Load `src/RemoteServer/Data/Migrations/schema.sql` into the `remoteserver` database. |
| Agent enrolls but console cannot connect | Check that the RemoteAgent service is running and that the SSH bastion host key is present in `bastion.env`. |
| `/agent` or `/api/*` returns 403 | Check nginx mTLS configuration and `/etc/nginx/client-ca.crt`. |
| `/admin/*` is unreachable from the internet | Expected. Use the console through an enrolled device tunnel. |
| TightVNC is not installed | Make sure `vnc\tightvnc.msi` exists next to the agent before service start, or run `RemoteAgent.exe provision-vnc --msi path\to\tightvnc.msi` elevated. |

## Final Checklist

- [ ] `/tmp/remoteserver.tar.gz` exists on the server.
- [ ] `deploy/setup-db.sh` completed.
- [ ] `schema.sql` loaded into MariaDB.
- [ ] `deploy/deploy-server.sh` completed and `remoteserver` is running.
- [ ] `Server__PublicUrl=https://your-domain` is set in `/etc/remoteserver/bastion.env`.
- [ ] `deploy/setup-bastion.sh` completed.
- [ ] TLS certificate exists for the public domain.
- [ ] `deploy/setup-nginx.sh` completed and `sudo nginx -t` passes.
- [ ] `deploy/harden.sh` completed after key-based SSH was verified.
- [ ] Temporary `admin` password was collected from the journal.
- [ ] `RemoteServer mint-blob` printed a bootstrap blob.
- [ ] First Windows device enrolled and appears as `Pending`.
- [ ] First admin signed in, changed password, configured TOTP, and approved the device.
