<p align="center">
  <img src="icon/RemoteAccessBlue_256x256.png" width="128" alt="RemoteAppClient">
</p>

<h1 align="center">RemoteAppClient</h1>

<p align="center">
  Self-hosted <b>remote access and fleet management</b> for Windows devices, built on .NET 10.
</p>

<p align="center">
  <a href="https://github.com/v1k70rk4/RemoteAppClient/actions/workflows/build.yml"><img src="https://github.com/v1k70rk4/RemoteAppClient/actions/workflows/build.yml/badge.svg" alt="build"></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white" alt=".NET 10">
  <img src="https://img.shields.io/badge/agent-Windows-0078D6?logo=windows&logoColor=white" alt="Windows agent">
  <img src="https://img.shields.io/badge/server-Linux-FCC624?logo=linux&logoColor=black" alt="Linux server">
  <img src="https://img.shields.io/badge/version-1.7.0-2ea44f" alt="version 1.7.0">
  <img src="https://img.shields.io/badge/UI-MaterialSkin-7E57C2" alt="MaterialSkin">
  <a href="https://v1k70rk4.github.io/RemoteAppClient/"><img src="https://img.shields.io/badge/website-v1k70rk4.github.io-41bdf5?logo=github" alt="website"></a>
</p>

---

RemoteAppClient is a self-hosted remote support system. A Windows agent runs as a SYSTEM
service on managed devices, keeps an outbound control channel to the server, reports
telemetry, provisions TightVNC, and opens SSH tunnels on demand. The Linux server owns
enrollment, command signing, authentication, audit logging, release channels, update
packages, and MSI generation. The Windows console client is the operator/admin UI.

The desktop capture and encoding layer is intentionally not implemented here. TightVNC
does that work as a separate process. RemoteAppClient provides the control plane,
enrollment, tunnel orchestration, security model, fleet UI, and update workflow around it.

Use this only on systems you own or are explicitly authorized to administer.

[Website and screenshots](https://v1k70rk4.github.io/RemoteAppClient/) |
[Deployment guide](#deployment-flow)

---

## Contents

- [What's New in 1.8.5](#whats-new-in-185)
- [What's New in 1.8.0](#whats-new-in-180)
- [What's New in 1.7.0](#whats-new-in-170)
- [What's New in 1.6.0](#whats-new-in-160)
- [What It Does](#what-it-does)
- [Architecture](#architecture)
- [Projects](#projects)
- [Security Model](#security-model)
- [Configuration](#configuration)
- [Linux Operator Console](#linux-operator-console)
- [Deployment Flow](#deployment-flow)
- [Build](#build)
- [Runtime Commands](#runtime-commands)
- [Release Packages](#release-packages)
- [Repository Layout](#repository-layout)
- [TightVNC And Licensing](#tightvnc-and-licensing)

---

## What's New in 1.8.5

A fleet **reliability and observability** release. **No database schema change since 1.8.0.**

**Agent liveness over a named pipe**
- The Helper (updater) now reads agent liveness from the agent's read-only status pipe
  (`RemoteAgent.status` → `LastHeartbeatUtc`) instead of a heartbeat file, removing a file-race that could
  report a bogus multi-billion-second "stale heartbeat" and force an unnecessary agent restart.
- A two-poll confirmation keeps a single transient blip from restarting a healthy agent. The legacy
  heartbeat file is still written for an older, file-based Helper during a rolling update and **self-retires**
  once the co-located Helper is the new pipe-aware build.

**Flaky-link detection (observability only)**
- The device list now tells **"alive but on a poor network"** apart from **"offline / dead"**: a device with
  frequent C2 reconnects shows as **`◐ flaky`** (amber) instead of **`○ offline`** (grey), with the reconnect
  count in the tooltip and a *Link* row in the telemetry panel.
- Computed **server-side** from C2 connection churn (in-memory, last hour). It is **pure observability and
  never triggers a restart**, needs no schema change, and is backward/forward compatible (older clients
  ignore the new field; an older server leaves it dormant).

---

## What's New in 1.8.0

1.8.0 adds **agentless operator consoles for Linux and Windows** and hardens the keyless sign-in path.
Highlights since 1.7.0:

**Operator consoles for Linux & Windows (new)**
- Two viewer-only consoles — **"Multiserver Linux RemoteAppClient Lite"** (Avalonia; `.deb` + AppImage) and a
  **Windows Lite** (WinForms; portable single-file `.exe`). Both show only **Devices, Settings, and About** —
  no admin features, even for admin accounts.
- **Server-independent / multi-server**: you type the server at sign-in; nothing is installed on the operator's
  machine and no agent is required there.
- **Agentless transport**: on sign-in the server mints a **short-lived operator SSH certificate** (gated by the
  per-account *keyless-operator* flag) and the console opens its own bastion tunnel — no local SYSTEM agent.
  This deliberately relaxes the Windows-SYSTEM rule for the separate Lite build only; the full client is unchanged.
- The Linux console is fully **localized (hu/en) with a language switch**, and both consoles show a
  **GitHub-release update notice** (portable clients have no self-update).

**Security**
- The **keyless-operator flag** (per account, set from the Windows console: Users → user → *Keyless operator*)
  gates the new consoles; off by default.
- **8-hour operator session + certificate** (one work day), aligned and auto-expiring.
- **Keyless brute-force protection**: failed sign-ins and password-recovery from a keyless source are
  rate-limited **by source IP** — a synthetic, auto-expiring lock that **never locks the user account** — with
  the **real client IP** recorded in the audit (operator-cert mints and failures show who, when, and from where).
- **Dependabot** (dependency updates) and **CodeQL** (code scanning) run in CI.

**Under the hood**
- Shared **`RemoteClient.Core`** (net10.0, no Windows deps) is reused by all three clients; `DevicesView` was
  decoupled from the agent broker via a forward delegate, so either transport drives the same view.
- New CI jobs build the Linux `.deb` and the Windows Lite `.exe` and attach them to tagged releases.
- .NET 10, EF Core 9 + Pomelo (MariaDB). **One new column since 1.7.0: `Users.KeylessOperator`** (the
  keyless-operator flag). Fresh installs get it from `schema.sql`; upgrading from 1.7.0 needs a one-liner:
  `ALTER TABLE Users ADD COLUMN KeylessOperator tinyint(1) NOT NULL DEFAULT 0`. The IP-based lockout reuses
  the existing `Devices` lockout columns, so there is no `Devices` change.

---

## What's New in 1.7.0

1.7.0 is a big round focused on **reaching devices on locked-down networks** and **moving files**,
plus independent on-device privacy controls. Highlights since 1.6.0:

**File transfer (new)**
- A dedicated, Total Commander-style **two-pane file manager**: the operator's local PC on the left,
  the remote device on the right, each with a drive selector and a file list.
- Full operations with multi-select: **copy both ways, new folder, delete, rename** — with a live
  per-file progress bar and a **Cancel** button (a cancelled transfer cleans up its partial file).
- It rides the same SSH reverse tunnel as VNC, so it inherits the transport and works behind
  DPI/Cloudflare **over WSS**. The remote pane opens at the signed-in user's home folder.
- Gated by the existing consent model plus a **per-session token**, loopback-only on the device, and
  every operation is audited. Available to admins from the device right-click menu.

**Connectivity: everything on 443**
- **Per-device bastion transport**: `auto`, `443 (sslh)`, `22 (SSH)`, or `443 (WSS)`, pushed to the
  device and applied without a reinstall. `auto` tries 443 first and falls back to WSS. A BETA-channel
  tab exposes the selector, and the About page shows the active transport.
- **SSH-over-WebSocket (`wss443`)**: the reverse tunnel is wrapped in a WebSocket to the server's
  `/ssh` endpoint, so remote access keeps working through DPI, proxies, and Cloudflare-style 443-only paths.
- **443 multiplexing via the nginx stream module** (TLS → the HTTPS app, SSH → sshd), with the **real
  client IP preserved** end-to-end (PROXY protocol + real_ip), replacing the previous muxer that hid it.
- Public IPs now show a cached **reverse-DNS name** ("name (ip)") in the device list and telemetry,
  flagged red on carrier-NAT addresses. Telemetry also shows the live connect path, e.g.
  `WSS <-> Bastion <-> WSS`.

**On-device privacy controls**
- The local lock now covers **VNC and file transfer independently**: a person at the machine can
  disable remote access, file transfer, or both (via UAC). Neither can be re-enabled remotely.

**Fleet & reliability**
- Non-blocking agent self-update and an auto-converge circuit breaker.
- A command-expiry watcher that no longer expires still-queued commands, so **offline devices keep
  their pending-update indicator** and update when they reconnect.
- Auto-sizing device-list columns and assorted UI polish.

**Under the hood**
- .NET 10, EF Core 9 + Pomelo (MariaDB). Two new `Devices` columns since 1.6.0 (`BastionTransport`,
  `PublicIpReverse`); fresh installs get them from `schema.sql`.

---

## What's New in 1.6.0

This release consolidates a large round of fleet, security, and UI work since the 1.5 line, and
unifies the database schema into a single baseline migration.

**Remote sessions**
- Two operators can share one machine over a single VNC tunnel (the server runs AlwaysShared).
- A session side panel pinned next to the viewer: an editable device note on top, live-refreshing
  telemetry below. Three layouts: 80/20 split, 100/20 background, or off.
- Per-operator, roaming viewer preferences (stored on the account): scale (defaults to fit-to-window)
  and colour depth, including a 256-colour fast mode.

**Security & access**
- Device trust ("remember this device"): skip TOTP for 90 days on a trusted machine; the password is
  always required. Admins can list and revoke a user's trusted devices.
- Consent model simplified to a single "consent required" switch, with correct detection of the
  signed-in session over RDP.
- Availability prompt ("Is your machine free now?") before connecting, with a timed wait.

**Fleet & updates**
- Auto-converge: an uploaded package becomes the channel's target version, so devices enrolled or
  approved after a rollout still update; components update one at a time per device in a safe order.
- Channels view: rollout indicator plus a sortable device component-versions table that auto-refreshes;
  telemetry interval lowered to one minute.
- Hardware telemetry: manufacturer, model, and serial number (SMBIOS, with OEM-placeholder handling).
- **Server self-update from the console** (Server settings → Server update): upload a `RemoteServer`
  tar.gz and an optional schema `upgrade.sql`, then update or roll back. A privileged systemd helper
  takes a full backup (binaries + DB dump) first, applies the upgrade, swaps the build, health-checks,
  and **auto-rolls-back on failure**. Needs a one-time helper install on the server box; the server
  process itself never gets sudo (it only drops a trigger that systemd acts on).

**Admin UI**
- Users tab: delete user, right-click actions, and a tabbed editor (general, password, permissions,
  log, Windows Hello, trusted devices).
- Server settings: owner and support branding plus e-mail sending (SMTP or Microsoft Graph app-only)
  with a test-send button.
- Devices search now matches group names; auto-sizing columns; many layout and polish fixes.

**Under the hood**
- Database schema consolidated into a single 1.6.0 baseline migration and regenerated `schema.sql`.
- .NET 10, EF Core 9 + Pomelo (MariaDB).

---

## What It Does

Remote access:

- Lists enrolled devices with online state, user, local/public IP, component versions,
  boot time, VPN/Wi-Fi state, local lock state, and recent incidents.
- Opens a reverse SSH tunnel from the target agent to the bastion and launches TightVNC
  through that bastion port.
- Supports consent prompts, unattended access policy, and a local "disable remote access"
  lock on the endpoint.
- Sends availability questions and operator messages to the signed-in device user.
- Supports power actions such as restart, forced restart, cancel shutdown, and sign out.

Fleet management:

- Device groups with inherited access policy.
- Admin/operator roles with device and group grants.
- User management, password reset, password recovery tokens, TOTP, and optional Windows
  Hello sign-in.
- Audit log for access, enrollment, package, user, settings, and security events.
- Server branding: owner name, support phone, and support email.

Operations:

- Release channels: `rtm` and `beta`.
- Components: `agent`, `updater`, `client`, and `vnc`.
- Upload packages, roll out by channel, promote beta packages to RTM, and update devices.
- Build per-group MSI installers on the server with agent, updater, optional console
  client, optional Start menu shortcut, bundled TightVNC MSI, and an embedded bootstrap blob.
- Localized UI/log strings with English and Hungarian dictionaries.
- Shared runtime language setting for all local executables on a device.

---

## Architecture

```text
                         Linux server
                  +------------------------+
                  | RemoteServer           |
                  |                        |
Windows agent --->| /agent  WSS + mTLS     |  signed commands
  SYSTEM          | /api/*  mTLS           |  telemetry, VNC secret, update packages
                  | /enroll token based    |
                  | /auth/* login/TOTP     |
                  | /admin/* localhost     |
                  +-----------+------------+
                              |
                              | MariaDB
                              |
                              v
                         audit, users,
                         devices, packages

Windows agent -- ssh -R --> bastion localhost port --> console client --> TightVNC viewer
Console client -- named pipe --> local agent broker -- ssh -L --> bastion/admin API
```

Important routing rules:

- Agents initiate outbound connections. Managed Windows devices do not need inbound ports.
- `/agent` is a WebSocket command channel and requires a client certificate.
- `/api/*` is for telemetry, VNC secret reporting, and update package downloads. It is
  protected by mTLS in the nginx deployment.
- `/admin/*` is localhost-only on the server. The console reaches it through a local
  agent broker, which opens `ssh -L` using the enrolled device identity.
- The operator console does not own an SSH key for the bastion. The local agent opens the
  forward through a named pipe broker, and server-side auth/grants still decide what the
  signed-in user may do.
- The target VNC service remains on the endpoint loopback. It is exposed only through an
  agent-created reverse tunnel.

---

## Projects

| Project | Purpose | Runs on |
|---|---|---|
| `RemoteAgent` | Windows service: enrollment bootstrap, command channel, command verification, telemetry, VNC provisioning, local broker, reverse tunnel, updates | Managed Windows devices as SYSTEM |
| `RemoteAgent.Updater` | Helper service: watchdog, heartbeat monitoring, and executable replacement for agent updates | Managed Windows devices as SYSTEM |
| `RemoteClient` | WinForms/MaterialSkin console for operators and administrators | Windows admin/operator devices |
| `RemoteServer` | ASP.NET Core server: enrollment, auth, admin API, command signing, telemetry ingest, package storage, MSI generation, audit | Linux |
| `RemoteAgent.Contracts` | Shared DTOs and canonical command signature logic | Referenced by client, agent, and server |
| `RemoteAgent.Resources` | Shared resources | Referenced by application projects |

---

## Security Model

RemoteAppClient treats command authenticity as mandatory:

- Agent identity is based on per-device client certificates issued during enrollment.
- The agent pins the server TLS certificate fingerprint.
- Server commands are signed with ECDSA P-256.
- The agent verifies every command against the enrolled server command-signing public key.
- Command replay is limited with nonce tracking plus a timestamp window.
- The server can choose only the remote bastion port for a tunnel. Bastion host, user,
  SSH key, SSH certificate, host key, and local target port come from device config.
- The bastion host key is pinned through a private known_hosts file created per SSH session.
- `/admin/*` is not exposed externally by the nginx deployment.
- Console sessions still require username/password plus TOTP unless Windows Hello is used.
- Operators see only granted devices/groups and can open tunnels only for those devices.
- Passwords are hashed with Argon2id.
- Database secrets such as VNC passwords, SMTP password, and Graph client secret are
  encrypted at rest with AES-256-GCM using a server-side key file.
- Enrollment tokens are stored as hashes. The raw token is visible only when generated.
- Bootstrap/MSI enrollment tokens create Pending devices by default; an administrator must
  approve them.
- The local VNC lock can disable remote access on a device and cannot be undone remotely.

Deployment assumption: Kestrel listens on localhost behind nginx. Do not expose the
RemoteServer Kestrel port directly to the internet.

---

## Configuration

RemoteAppClient has four configuration surfaces:

1. Server appsettings and environment variables.
2. Agent appsettings plus enrollment-generated files.
3. Console client local settings.
4. Shared per-machine language settings.

### Server Configuration

Server settings are read from `src/RemoteServer/appsettings.json` and environment variables.
The deployment scripts write sensitive values into `/etc/remoteserver/*.env`, consumed by
the systemd service through `EnvironmentFile`.

Environment variables use ASP.NET Core double-underscore syntax:

```bash
Server__PublicUrl=https://remote.example.com
Server__Bastion__Host=remote.example.com
ConnectionStrings__MariaDb="Server=localhost;Port=3306;Database=remoteserver;User Id=remoteserver;Password=..."
```

| Setting | Default | Required | Description |
|---|---:|:---:|---|
| `ConnectionStrings:MariaDb` | `CHANGEME` sample | Yes | MariaDB connection string. `deploy/setup.sh` (step 02) writes this to `/etc/remoteserver/db.env`. |
| `Server:CommandSigningKeyPath` | `/etc/remoteserver/cmd_signing.key` sample | Yes | ECDSA P-256 private key used to sign agent commands. The public key is returned to agents during enrollment. |
| `Server:CaCertPath` | `/etc/remoteserver/ca.crt` | Yes | Client-certificate CA public certificate. nginx also uses this for mTLS validation. |
| `Server:CaKeyPath` | `/etc/remoteserver/ca.key` | Yes | Client-certificate CA private key. Keep readable only by the service user. |
| `Server:ClientCertValidityDays` | `825` | No | Validity period for issued agent client certificates. |
| `Server:SecretKeyPath` | `/etc/remoteserver/secret.key` | Yes | 32-byte AES-GCM key for database secret encryption. |
| `Server:PackagesDir` | `/var/lib/remoteserver/packages` | Yes | Persistent package store for uploaded EXE/MSI files and generated MSI installers. |
| `Server:PublicUrl` | empty | Yes for bootstrap/MSI | Public base URL embedded into bootstrap blobs, for example `https://remote.example.com`. |
| `Server:MinClientVersion` | `1.5.0.0` | No | Oldest console client allowed to sign in. Older clients receive a mandatory update response. Empty disables the gate. |
| `Server:MsiSigning:CertPath` | empty | No | Optional Authenticode signing certificate path for generated MSI files. Empty means unsigned MSI. |
| `Server:MsiSigning:Password` | empty | If signing | Password for the MSI signing PFX. Prefer environment/secret storage. |
| `Server:MsiSigning:TimestampUrl` | `http://timestamp.digicert.com` | No | Timestamp server used by `osslsigncode`. |
| `Server:Bastion:Host` | empty | Yes | Public SSH bastion host returned to agents during enrollment. |
| `Server:Bastion:Port` | `22` | No | SSH bastion port. |
| `Server:Bastion:User` | `agent` | No | Restricted SSH user used by enrolled agents. |
| `Server:Bastion:HostKey` | empty | Yes | Bastion host key in known_hosts format without the trailing comment, for example `ssh-ed25519 AAAA...`. |
| `Server:Bastion:SshCaKeyPath` | `/etc/remoteserver/agent_ca` | Yes | SSH CA private key used to sign agent SSH keys. |
| `Server:Bastion:SshCertValidityDays` | `825` | No | Validity period for agent SSH certificates. |
| `Server:Bastion:TunnelPortMin` | `50000` | No | First bastion port used for stable per-device reverse tunnels. |
| `Server:Bastion:TunnelPortMax` | `60000` | No | Exclusive upper bound of the per-device tunnel port range. |

Server-side values stored in the database:

- Owner name, support phone, support email.
- Email provider: none, SMTP, or Microsoft Graph.
- SMTP password and Graph client secret, encrypted with `SecretProtector`.
- Graph secret expiry date and notification state.
- Users, roles, grants, devices, groups, packages, tokens, audit logs, and sessions.

### Agent Configuration

The agent reads `Agent` options from appsettings, environment variables, and then
overrides most runtime values from `C:\ProgramData\RemoteAgent\enrollment.json` after
enrollment. In normal MSI/bootstrap installs, you should not hand-edit most of these.

Local agent data directory by default:

```text
C:\ProgramData\RemoteAgent
```

Important generated files:

| File | Purpose |
|---|---|
| `enrollment.json` | Device ID, server URL, command-signing public key, bastion host/user/host key. |
| `agent.pfx.dat` | DPAPI-protected client certificate PFX for mTLS. |
| `ca.crt` | Server/client CA certificate returned during enrollment. |
| `id_ed25519` | Agent SSH private key for bastion tunnels. ACL is restricted to SYSTEM and Administrators. |
| `id_ed25519-cert.pub` | SSH certificate signed by the server-side SSH CA. |
| `bootstrap.dat` | Optional one-time bootstrap blob consumed on first start. |
| `vnc.secret` | Local VNC password cache used for reporting to the server. |

| Setting | Default | Usually set by | Description |
|---|---:|---|---|
| `Agent:AgentId` | empty | enrollment | Stable device identifier. Empty means derived locally until enrollment supplies the server device ID. |
| `Agent:EnrollmentDir` | `C:\ProgramData\RemoteAgent` | appsettings/MSI | Directory for enrollment output and runtime state. |
| `Agent:ClientCertPfxPath` | empty | enrollment | DPAPI-protected PFX path used for mTLS. |
| `Agent:CommandChannel:Url` | sample URL | enrollment | WSS endpoint, for example `wss://remote.example.com/agent`. |
| `Agent:CommandChannel:ClientCertThumbprint` | empty | legacy/manual config | Store thumbprint fallback when no PFX path is configured. |
| `Agent:CommandChannel:ServerCertPinSha256` | empty | enrollment/manual | SHA-256 pin of the server TLS certificate. |
| `Agent:CommandChannel:CommandSigningPublicKey` | empty | enrollment | Base64 SPKI public key used to verify signed commands. |
| `Agent:CommandChannel:MaxCommandAgeSeconds` | `60` | appsettings | Replay window for signed commands. |
| `Agent:CommandChannel:ReconnectBaseDelaySeconds` | `2` | appsettings | Initial reconnect delay after command channel failure. |
| `Agent:CommandChannel:ReconnectMaxDelaySeconds` | `120` | appsettings | Maximum reconnect delay. |
| `Agent:CommandChannel:KeepAliveIntervalSeconds` | `15` | appsettings | WebSocket keepalive interval. |
| `Agent:CommandChannel:KeepAliveTimeoutSeconds` | `10` | appsettings | Pong timeout before reconnecting stale connections. |
| `Agent:Tunnel:BastionHost` | sample host | enrollment | SSH bastion hostname. |
| `Agent:Tunnel:BastionPort` | `22` | enrollment | SSH bastion port. |
| `Agent:Tunnel:BastionUser` | `agent` | enrollment | Restricted SSH user. |
| `Agent:Tunnel:PrivateKeyPath` | `C:\ProgramData\RemoteAgent\id_ed25519` | enrollment/appsettings | Agent SSH private key. |
| `Agent:Tunnel:CertificatePath` | empty | enrollment | Agent SSH certificate path. |
| `Agent:Tunnel:BastionHostKey` | empty | enrollment | Pinned bastion host key. |
| `Agent:Tunnel:LocalForwardPort` | `5900` | appsettings | Local target port exposed through reverse tunnel, normally TightVNC. |
| `Agent:Tunnel:IdleTimeoutSeconds` | `1800` | appsettings | Auto-close idle remote access tunnel. |
| `Agent:Tunnel:SshExecutablePath` | `C:\Windows\System32\OpenSSH\ssh.exe` | appsettings | `ssh.exe` path. Empty means resolve from PATH. |
| `Agent:Telemetry:IngestUrl` | sample URL | enrollment | Telemetry endpoint, for example `https://remote.example.com/api/telemetry`. |
| `Agent:Telemetry:ClientCertThumbprint` | empty | legacy/manual config | Store thumbprint fallback for telemetry mTLS. |
| `Agent:Telemetry:ServerCertPinSha256` | empty | enrollment/manual | TLS pin for telemetry HTTP calls. |
| `Agent:Telemetry:IntervalSeconds` | `300` | appsettings | Telemetry interval. |

### Console Client Configuration

The console client stores local user settings here:

```text
%APPDATA%\RemoteClient\config.json
```

Current client flow:

- It first connects to the local agent broker named pipe: `RemoteAgent.broker`.
- The agent broker opens `ssh -L` forwards to the bastion.
- The client talks HTTP to the forwarded localhost port.
- The client launches TightVNC viewer for remote desktop sessions.

| Setting | Default | Description |
|---|---:|---|
| `AdminApiPort` | `5000` | Bastion loopback port for the server admin API. The local agent broker forwards this. |
| `ViewerExe` | `C:\Program Files\TightVNC\tvnviewer.exe` | TightVNC viewer path used by the console. |
| `ThemeMode` | `dark` | `light`, `dark`, or `auto`. |
| `Channel` | `rtm` | Console self-update channel, `rtm` or `beta`. |
| `HelloCredentialId` | null | Windows Hello credential registered for this local client/user. |
| `HelloUsername` | null | Username associated with the local Windows Hello credential. |
| `SshHost`, `SshUser`, `SshPort`, `SshKeyPath`, `SshExe` | legacy/manual fields | Present in the config model, but the current console path uses the local agent broker instead of a client-owned SSH key. |

### Shared Language Setting

All local executables read the shared language preference from:

```text
%ProgramData%\RemoteAppClient\settings.json
```

Supported values:

| Value | Meaning |
|---|---|
| `auto` | Use the system UI culture. |
| `en` | English. |
| `hu` | Hungarian. |

The console Settings page writes this file. Running services and already-open windows may
need a restart to fully pick up the new language.

### Deployment Script Outputs

`deploy/setup.sh` runs as ordered steps under `deploy/steps/`. The values they generate
should not be committed:

| Step | Output |
|---|---|
| `02-mariadb` | MariaDB database/user (or your external DB) and `/etc/remoteserver/db.env`. |
| `03-schema` | Baseline schema loaded into the database. |
| `04-server` | `/opt/remoteserver`, command-signing key, client CA, `secret.key`, `bastion.env` (+ `PublicUrl`), systemd unit, package directory. |
| `05-bastion` | Restricted `agent` SSH user, SSH CA key, sshd Match block. |
| `06-tls` | Let's Encrypt certificate via Cloudflare DNS-01. |
| `07-nginx` | 443 multiplexer (SSH + HTTPS/WSS on one port), mTLS for `/agent` `/ssh` `/api/*`, localhost-only `/admin/*`. |
| `08-harden` | UFW, fail2ban, and sshd hardening. |
| `09-blob` | First bootstrap blob + admin login. |
| `10-selfupdate` | Console-driven server self-update helper (systemd path-units + root helper scripts). |

`deploy/fetch-tightvnc.sh` (run separately) downloads the pinned TightVNC MSI and the
matching GPL source ZIP under `third_party/tightvnc`.

---

## Linux Operator Console

**Multiserver Linux RemoteAppClient Lite** is a viewer-only operator console for Linux
(Debian/Ubuntu). It connects to the same server as the Windows console and opens VNC
sessions to enrolled devices, but it runs **no agent** and can do nothing on the remote
side beyond what an operator already can.

How it differs from the Windows console:

- **No local SYSTEM agent / broker.** On sign-in the server mints a short-lived (12 h)
  operator SSH certificate for an ephemeral key, and the console opens its own `ssh -L`
  forwards to the bastion with that cert — there is no client-owned long-lived SSH key.
- **Per-account gate.** The account must have the **keyless-operator** flag, set in the
  Windows console (Users → pick the user → *Keyless operator (Linux console)*). Without it
  the server mints no certificate and sign-in is refused.
- **Viewer only.** Device list, telemetry, VNC connect, and password recovery — no device
  management, MSI, channels, or server administration.

### Requirements

| Need | Package |
|---|---|
| SSH client (tunnels) | `openssh-client` |
| VNC viewer | `ssvnc` (preferred — adds fit-to-window scaling + 256-color) or `tigervnc-viewer` |

Both are declared as dependencies of the `.deb`.

### Install

```bash
sudo apt install ./remoteclient_<version>_amd64.deb     # or run the AppImage directly
```

The `.deb` and AppImage are built from a self-contained `linux-x64` publish:

```bash
dotnet publish src/RemoteClient.Linux/RemoteClient.Linux.csproj -c Release -r linux-x64 --self-contained -o publish/client-linux
src/RemoteClient.Linux/packaging/build-deb.sh      publish/client-linux <version> icon/RemoteAccessBlue_256x256.png .
src/RemoteClient.Linux/packaging/build-appimage.sh publish/client-linux <version> icon/RemoteAccessBlue_256x256.png .
```

CI builds the `.deb` on every push (the `client-linux` job) and attaches it to tagged releases.

### Usage

Launch *Multiserver Linux RemoteAppClient Lite*, sign in (server URL, username, password,
and TOTP when required), pick a device, and **Connect (VNC)**. The device table (hostname,
group, note, online, last-seen) is sortable and searchable. **Settings** adjusts the VNC
scaling and 256-color mode plus the UI language (Auto / English / Magyar; the language
applies after a restart). **Forgot password?** runs the same code-based recovery as the
Windows console.

---

## Deployment Flow

### Server (one command, on a fresh Ubuntu box)

`deploy/setup.sh` takes a fresh server from zero to a running `RemoteServer` — database, server,
bastion, TLS, the **443 multiplexer**, hardening, the **self-update helper**, and the first
bootstrap blob — then verifies it. Run it **on the server**, as a user with passwordless sudo:

```bash
git clone https://github.com/v1k70rk4/RemoteAppClient.git
cd RemoteAppClient
./deploy/setup.sh
```

It asks a few things up front — public DNS name, ACME email, and **whether you have your own
MariaDB** (otherwise it installs one) — then runs to the end. It is idempotent, and each step
under `deploy/steps/` can run on its own, e.g. `./deploy/setup.sh 06-tls 07-nginx`. For an
unattended run, copy `deploy/config.env.example` to `deploy/config.env` and fill it in. Details:
[`deploy/README.md`](deploy/README.md).

TLS uses Let's Encrypt with Cloudflare DNS-01 — the script prompts for a Cloudflare API token
(Zone → DNS → Edit), or place it at `/etc/letsencrypt/cloudflare.ini` first. No Cloudflare? Issue
the certificate yourself and skip `06-tls`. At the end, `09-blob` prints the bootstrap blob **and
the first admin login** (`admin` + a temporary password to change on first sign-in).

### First Windows device

For the first machine, manual enrollment is simplest; later, build group MSIs from the console.
In an elevated PowerShell, put the release artifacts in one folder and run:

```powershell
.\RemoteAgent.exe bootstrap "<blob>"
.\RemoteAgent.exe install-service --owner "Company Name" --group "First devices"
```

To provision TightVNC on first start, place the pinned MSI at `.\vnc\tightvnc.msi` first (see
[TightVNC And Licensing](#tightvnc-and-licensing)). The agent enrolls on first start, opens its
tunnel, and reports telemetry. Because the bootstrap token is not auto-approved, the device stays
**Pending** until approved.

### First sign-in

Start `RemoteClient.exe`, sign in as `admin` with the temporary password, complete the forced
password change and TOTP setup, then approve the Pending device in the Devices view. From there
you can upload packages, build group MSI installers, and use the normal remote workflow.

### Troubleshooting

```bash
sudo systemctl status remoteserver
sudo journalctl -u remoteserver -f
sudo nginx -t && sudo tail -f /var/log/nginx/error.log
```

| Symptom | Check |
| --- | --- |
| `09-blob` reports missing `PublicUrl` | Set `Server__PublicUrl` in `/etc/remoteserver/bastion.env`, restart `remoteserver`. |
| `09-blob` reports missing schema | Re-run `./deploy/setup.sh 03-schema` (`RAC_SCHEMA_FORCE=1` to reload). |
| Agent enrolls but console cannot connect | RemoteAgent service running? Bastion host key present in `bastion.env`? |
| `/agent` or `/api/*` returns 403 | Check nginx mTLS and the client CA. |
| `/admin/*` unreachable from the internet | Expected — localhost-only, reached through an enrolled device tunnel. |
| TightVNC not installed | Ensure `vnc\tightvnc.msi` sits next to the agent before service start, or run `RemoteAgent.exe provision-vnc --msi <path>` elevated. |

---

## Build

Requirements:

- .NET 10 SDK.
- Windows for the Windows-targeted projects (`RemoteAgent`, `RemoteAgent.Updater`, `RemoteClient`).
- Linux or Windows for the server project.
- `wixl` on Linux when building MSI packages server-side.
- `osslsigncode` only if MSI Authenticode signing is configured.

Build all Windows components as single-file, self-contained EXEs:

```powershell
.\build.ps1
```

Default output:

```text
C:\RAC\build
```

Build the server:

```bash
dotnet publish src/RemoteServer/RemoteServer.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained \
  -o publish/server

tar -C publish/server -czf RemoteServer-linux-x64.tar.gz .
```

Run a development build:

```powershell
dotnet build RemoteAppClient.slnx
```

---

## Runtime Commands

Agent commands:

```powershell
RemoteAgent.exe enroll --token <token> --server https://remote.example.com [--hostname NAME] [--out C:\ProgramData\RemoteAgent]
RemoteAgent.exe bootstrap "<blob>"
RemoteAgent.exe install-service [--owner "Company"] [--group "Group"]
RemoteAgent.exe uninstall-service
RemoteAgent.exe provision-vnc [--msi path\to\tightvnc.msi] [--password <password>]
RemoteAgent.exe remove-vnc
RemoteAgent.exe vnc-lock
RemoteAgent.exe vnc-unlock
```

Server first-run helper:

```bash
/opt/remoteserver/RemoteServer mint-blob
```

`mint-blob` checks database/schema, first admin, command-signing key, CA, `PublicUrl`,
and bastion configuration before printing the first bootstrap blob.

---

## Release Packages

`.github/workflows/build.yml` publishes artifacts on `v*` tags:

| File | Use |
|---|---|
| `RemoteAgent.exe` | Upload as the `agent` package. |
| `RemoteAgent.Updater.exe` | Upload as the `updater` package. |
| `RemoteClient.exe` | Upload as the `client` package. |
| `RemoteServer-linux-x64.tar.gz` | Deploy to the Linux server with `deploy/setup.sh` (step `04-server`). |

Typical release:

```bash
git tag v1.7.0
git push origin v1.7.0
```

The server stores uploaded packages in `Server:PackagesDir`, computes SHA-256, and uses
the hash in signed update commands. Agents verify package hashes before staging or
installing updates.

---

## Repository Layout

```text
src/
  RemoteAgent/             Windows agent service
  RemoteAgent.Updater/     helper/watchdog/update service
  RemoteClient/            WinForms operator/admin console
  RemoteServer/            ASP.NET Core server
  RemoteAgent.Contracts/   shared DTOs and command signatures
  RemoteAgent.Resources/   shared resources

deploy/                    from-scratch server installer
  setup.sh                 orchestrator that runs the steps below
  lib.sh
  config.env.example
  steps/                   01-prereqs … 11-verify
  fetch-tightvnc.sh        pinned TightVNC MSI + GPL source
  README.md

icon/
third_party/
.github/workflows/build.yml
build.ps1
RemoteAppClient.slnx
```

---

## TightVNC And Licensing

RemoteAppClient is released under the [MIT License](LICENSE).

RemoteAppClient uses TightVNC as a separate process for screen capture and remote desktop.
The repository does not commit the TightVNC binary. `deploy/fetch-tightvnc.sh` downloads a
pinned version, verifies SHA-256, and also downloads the corresponding source ZIP for GPL
compliance.

TightVNC remains under its own GPLv2 license. RemoteAppClient interacts with it as an
installed external program and MSI package.
