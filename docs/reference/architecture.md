---
title: Architecture
description: The Windows agent, the Linux server and the consoles - endpoints, routing rules, and the projects of the repository.
---

# Architecture

```text
                         Linux server
                  +------------------------+
                  | RemoteServer           |
                  |                        |
Windows agent --->| /agent  WSS + mTLS     |  signed commands
  SYSTEM          | /api/*  mTLS           |  telemetry, access results, update packages
                  | /ssh    WSS + mTLS     |  SSH bridged over WebSocket (TLS-only networks)
                  | /enroll token based    |
                  | /auth/* login/TOTP     |
                  | /admin/* localhost     |
                  +-----------+------------+
                              |
                              | MariaDB
                              v
                         audit, users, devices,
                         groups, packages, events

Windows agent -- ssh -R --> bastion loopback port --> console --> TightVNC viewer
Console -- named pipe --> local agent broker -- ssh -L --> bastion / admin API
```

## Routing rules

- **Agents initiate every connection.** Managed Windows devices need no inbound ports.
- `/agent` is the WebSocket command channel; it requires the device's client certificate. The server pushes signed commands down it and the agent answers on `/api/*`.
- `/api/*` carries telemetry (every minute), access results, the VNC and SSH certificate requests and the update package downloads; nginx enforces mTLS on it.
- `/ssh` bridges SSH over a WebSocket for devices on networks that let only TLS through; the inner SSH still authenticates at the bastion.
- `/admin/*` is **localhost only** on the server. The Windows console reaches it through the local agent's broker, which opens `ssh -L` with the enrolled device's identity; the Lite and Linux consoles with a short-lived certificate of their own. Server-side authentication and grants decide what the signed-in user may do.
- **443 carries everything.** nginx's stream module tells TLS from raw SSH on the one port: TLS goes to the HTTPS server (which serves the WebSockets and the API), raw SSH goes to the bastion's sshd. Clients that cannot reach raw SSH use `/ssh` instead.
- The VNC server stays on the device's loopback. It is reachable only through the reverse tunnel the agent opened, and only by whoever holds the session's password.

## The projects

| Project | Purpose | Runs on |
|---|---|---|
| `RemoteAgent` | The Windows service: enrollment, the command channel and its signature checks, telemetry, VNC provisioning, the local broker, the reverse tunnel, the file service, the ssh-agent for the TPM key, updates | Managed Windows devices, as SYSTEM |
| `RemoteAgent.Updater` | The helper service: watchdog, heartbeat, executable replacement for agent updates | Managed Windows devices, as SYSTEM |
| `RemoteClient` | The WinForms console for operators and administrators | Windows admin and operator devices |
| `RemoteClient.Lite` | Viewer-only Windows console that signs in with an ephemeral SSH certificate; no local agent | Windows operator devices |
| `RemoteClient.Linux` | Viewer-only Avalonia console for Linux operators | Linux operator devices |
| `RemoteClient.Cli` | `racctl`: the server's log, health, fleet listings and self-update from the command line, with an access token | Enrolled Windows devices |
| `RemoteClient.Core` | Shared console logic: admin API client, tunnel transports, localization | Referenced by the consoles and `racctl` |
| `RemoteServer` | ASP.NET Core: enrollment, auth, admin API, command signing, telemetry ingest, package store, MSI generation, audit, diagnostics | Linux |
| `RemoteAgent.Contracts` | Shared DTOs and the canonical command signature | Referenced by client, agent and server |
| `RemoteAgent.Resources` | Shared resources | Referenced by the applications |

## Repository layout

```text
src/                       the projects above
deploy/                    from-scratch server installer: setup.sh, steps/01-prereqs … 11-verify,
                           backup.sh / restore.sh, fetch-tightvnc.sh, KEYS.md
tests/                     xunit tests (run in CI)
docs/ website/ mkdocs.yml  this website
icon/                      the application icon
third_party/               the pinned TightVNC MSI and its GPL source, fetched, not committed
.github/workflows/         build.yml (every push; release on v* tags), codeql, docs
build.ps1                  the Windows build: agent, updater, console, Lite, server package, signing
RemoteAppClient.slnx
```
