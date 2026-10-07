# Keys, certificates and what can be rotated

RemoteAppClient has no shared password between the server and its devices. Every device carries its own
certificate and SSH key, and pins three things about the server: the public key that signs commands, the SSH
host key of the bastion, and the CA that issued its certificate. That is what makes a stolen database useless
on its own - and what makes some keys impossible to rotate in place. This page lists every secret, where it
lives, what happens when it is lost, and how (or whether) it can be replaced.

## Inventory

### On the server

| Secret | Where | Protects | Lifetime | Rotation |
|---|---|---|---|---|
| Client CA key + certificate | `/etc/remoteserver/ca.key`, `ca.crt`; nginx trusts a copy at `/etc/nginx/client-ca.crt` | Issues every device's client certificate; nginx admits `/agent`, `/ssh` and `/api/` only with a certificate from this CA | CA: as created by setup; device certificates: `Server:ClientCertValidityDays` (825 days) from enrolment | **Not in place** - see *Fleet identity* |
| Command signing key | `/etc/remoteserver/cmd_signing.key` (ECDSA P-256) | Signs every command; the public key sits in each device's `enrollment.json`, an unsigned command is dropped | Unlimited | **Not in place** |
| Agent SSH CA | `/etc/remoteserver/agent_ca`, `agent_ca.pub`; sshd trusts `/etc/ssh/agent_ca.pub` | Signs the devices' SSH certificates for the reverse tunnels | Device SSH certificates: `Server:Bastion:SshCertValidityDays` (825 days) | **Not in place** |
| Bastion host key | `/etc/ssh/ssh_host_ed25519_key` | Devices pin it (`bastionHostKey` in `enrollment.json`); a different key means every tunnel refuses to connect | Unlimited | **Not in place** |
| Secret key | `/etc/remoteserver/secret.key` (32 random bytes, AES-256-GCM) | Encrypts what the database holds for you: VNC passwords, device notes, TOTP secrets | Unlimited | **Not supported yet** - see below |
| Database password | `/etc/remoteserver/db.env` | The server's MariaDB login (DML only since the role split) | Unlimited | Yes, any time |
| TLS certificate | `/etc/letsencrypt/live/<domain>/` | The public 443 | 90 days, renewed by certbot; the deploy hook reloads nginx | Automatic; the diagnostics snapshot and the health alerts watch the days left |
| E-mail credentials | `ServerSettings` (SMTP password or Graph client secret) | Password-recovery and alert mail | Graph secrets expire; the server warns 30 days ahead | In Entra / at the provider, then in *Server settings* |

### Operator side

| Secret | Lifetime | Revocation |
|---|---|---|
| Console session | 8 hours (the operator SSH certificate is minted to match) | *Users → Force sign-out* revokes every session of a user |
| "Remember this device" trust | 90 days; skips the second factor, never the password | Revoking the user's sessions revokes it too |
| Access token (`rac_…`) | Optional expiry; stored as a hash | *Diagnostics → Access tokens → Revoke*; revoking sessions revokes tokens |
| TOTP secret | Until cleared | *Users → Clear TOTP*; the user enrols again at the next sign-in |
| Windows Hello credential | Until revoked | *Users*, or the user's own device list |
| Password reset code | 30 minutes, single use | Expires by itself |
| Enrollment (bootstrap) token | Limited uses and optional expiry | *Enrollment tokens → Revoke / Delete* |

### On a device (`C:\ProgramData\RemoteAgent`)

| File | What it is |
|---|---|
| `enrollment.json` | Server URL, bastion host and host key, the command signing public key, the device id |
| `agent.pfx.dat` | The device's client certificate and private key, DPAPI-protected (machine scope) |
| `id_ed25519`, `id_ed25519-cert.pub` | The tunnel key and its SSH certificate from the agent CA |
| `vnc.secret` | The device's VNC password, DPAPI-protected; the server holds the same value encrypted with `secret.key` |
| `diag.json` | Present only while verbose logging is switched on |

## Rotation procedures

**Database password.** `ALTER USER 'remoteserver'@'localhost' IDENTIFIED BY '<new>'` as root, write the new
connection string into `/etc/remoteserver/db.env`, `systemctl restart remoteserver`. Nothing else holds it.

**TLS.** Nothing to do. If the snapshot shows fewer than 30 days left, certbot has stopped renewing: run
`certbot renew --dry-run` on the box and read the error.

**Access tokens, sessions, TOTP, Hello, enrollment tokens.** From the console, as in the table above. Every
revocation is an audit entry.

**E-mail secret.** Create the new secret at the provider, paste it into *Server settings*, send a test mail.

**Secret key (`secret.key`).** Not supported in place today: the stored VNC passwords, notes and TOTP secrets
are encrypted with it and would have to be re-encrypted under the new key. Until a re-encryption command
exists, a replaced key means:

- VNC passwords: every agent reports its own again when its service restarts (*Commands → Restart services*
  from the console, or the next reboot). Until then that device cannot be connected to.
- Device notes: lost; import them again from your `hostname;note` list (*Import notes*).
- TOTP secrets: every user enrols again at the next sign-in; clear them first so the sign-in asks for it.

**Fleet identity: client CA, command signing key, agent SSH CA, bastion host key.** These cannot be rotated
in place, because every device pins them: with a new CA nginx rejects the old certificates, with a new signing
key every command is dropped, with a new host key every tunnel refuses the bastion. Replacing any of them is a
migration, not a rotation: build a new MSI or bootstrap blob after the change and re-enrol every device,
knowing that a device still carrying the old identity drops off the moment the old key is gone. Plan it like
an OS replacement, in a maintenance window, device by device.

The practical defence is therefore to protect them rather than to rotate them: `deploy/backup.sh --encrypt`
puts all of them into one passphrase-sealed archive (keep it off the box, keep the passphrase elsewhere), the
box gives no root to anyone who does not need it, and the files are readable by the service user only.

## Expiry you have to plan for

**Device certificates expire 825 days after enrolment, and nothing renews them yet.** The same goes for the
SSH certificate issued at the same time. When they run out, nginx answers the device's requests with 403 and
sshd refuses its tunnel: the device shows as offline and stays so until it is re-enrolled (run the MSI or the
bootstrap blob on it again).

- The health alerts warn 60 days ahead (`Server:Alerts:DeviceCertDaysWarn`), naming the first device and the date.
- The enrolment time of a device is its `device.enrolled` entry in the audit log.
- `Server:ClientCertValidityDays` and `Server:Bastion:SshCertValidityDays` apply to future enrolments only.
- A renewal mechanism (the agent asking for a new certificate with its still-valid old one) is the intended
  fix; until it exists, re-enrolment in time is the procedure.

## What to back up

`deploy/backup.sh` captures the fleet identity (the four pinned keys and `secret.key`), the database, and the
bastion host key - about 2 KB of secrets plus the dump. With `--encrypt` the archive is sealed with a
passphrase; `deploy/restore.sh` recognises and decrypts it. The package directory is deliberately left out
(large, re-uploadable); after a restore, upload the agent, updater, client and vnc packages again - the
diagnostics snapshot lists what is missing.
