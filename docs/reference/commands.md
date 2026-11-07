---
title: Commands
description: The RemoteAgent.exe verbs for enrollment, the service, TightVNC and the locks, the server's first-run helper, and racctl.
---

# Commands

## `RemoteAgent.exe`

All verbs run in an elevated prompt on the device.

```powershell
RemoteAgent.exe enroll --token <token> --server https://remote.example.com [--hostname NAME] [--out C:\ProgramData\RemoteAgent]
RemoteAgent.exe bootstrap "<blob>"
RemoteAgent.exe install-service [--owner "Company"] [--group "Group"]
RemoteAgent.exe uninstall-service
RemoteAgent.exe provision-vnc [--msi path\to\tightvnc.msi] [--password <password>]
RemoteAgent.exe remove-vnc
RemoteAgent.exe vnc-lock
RemoteAgent.exe vnc-unlock
RemoteAgent.exe file-lock
RemoteAgent.exe file-unlock
```

| Verb | What it does |
|---|---|
| `enroll` | Enrolls with a token and the server URL: generates the device key (TPM where possible), gets the certificates and writes the enrollment files. |
| `bootstrap` | The same from a bootstrap blob, which carries the server URL, the token and the pins in one string. |
| `install-service` / `uninstall-service` | Installs or removes the agent and updater services; `--owner` and `--group` are shown to the user and sent at enrollment. |
| `provision-vnc` / `remove-vnc` | Installs the pinned TightVNC MSI (or the one given) with the agent's hardened configuration, or removes it. The service does the same on first start when the MSI sits at `vnc\tightvnc.msi`. |
| `vnc-lock` / `vnc-unlock`, `file-lock` / `file-unlock` | The local locks on remote screen access and on file transfer. Nothing remote undoes them; the console shows them, and the file lock ends a transfer in flight. |

## The server's first-run helper

```bash
/opt/remoteserver/RemoteServer mint-blob
```

Checks the database and schema, the first admin, the command-signing key, the CA, `PublicUrl` and the bastion configuration, then prints a bootstrap blob. `09-blob` runs it for you.

## `racctl`

```text
racctl token <token>            store the access token (DPAPI, current user)
racctl token --clear            forget the stored token
racctl logs [-n N] [--level info|warn|error|debug] [--since 30m|2h|1d|<iso>] [--grep TEXT] [--day YYYY-MM-DD]
racctl diag                     health snapshot (JSON)
racctl status                   self-update status (JSON)
racctl devices                  device table
racctl events <deviceId> [-n N] device history (JSON)
racctl audit [-n N]             audit log (JSON)
racctl get /admin/<path>        any read-only admin GET, raw

with a token minted for server update:
racctl update <server.tar.gz> [--sql upgrade.sql] [--no-wait]
racctl apply [--no-wait]
racctl rollback [--no-wait]
```

Exit codes: 0 ok, 1 error or failed update, 2 usage, 3 no local agent, 4 token rejected or out of scope, 5 server too old. `RACCTL_TOKEN` in the environment overrides the stored token.
