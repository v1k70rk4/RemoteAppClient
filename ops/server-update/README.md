# Server self-update helper

Privileged helper that performs console-driven server updates (Server settings → Server update tab).
The server process (`remotesrv`, non-root) only stages files and drops a trigger; these root systemd
units do the backup / stop / migrate / swap / start / health-check / rollback work in their own cgroup.

## Contents
- `deploy.sh` / `rollback.sh` — installed to `/opt/remoteserver-update/` (root, 0755).
- `remoteserver-update.{path,service}` / `remoteserver-rollback.{path,service}` — installed to
  `/etc/systemd/system/` (root, 0644). The `.path` units watch `apply.trigger` / `rollback.trigger`.
- `install.sh` — idempotent installer (creates dirs, installs the above, enables the watchers).

## Install (one-time, per server box)
```bash
scp -r ops/server-update <admin>@<host>:/tmp/
ssh <admin>@<host> 'sudo /tmp/server-update/install.sh'
```
Prerequisite: the server is already deployed (the `remotesrv` user, `/opt/remoteserver`, and the
`remoteserver.service` unit exist).

## Notes
- Backups live in `/var/lib/remoteserver/backups` (root-only, newest 3 kept).
- Health check hits `http://127.0.0.1:5000/health`; a build without that endpoint is treated as
  unhealthy and rolled back (it also lacks the self-update endpoints, so this is the safe outcome).
- The client shows a warning and disables Update/Rollback when the helper is not installed
  (`ServerUpdateStatus.HelperReady`).
