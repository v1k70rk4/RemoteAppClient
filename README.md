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
  <img src="https://img.shields.io/badge/version-1.5.11-2ea44f" alt="version 1.5.11">
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
[First-run guide](FIRST-RUN.md)

---

## Contents

- [What It Does](#what-it-does)
- [Architecture](#architecture)
- [Projects](#projects)
- [Security Model](#security-model)
- [Configuration](#configuration)
- [Deployment Flow](#deployment-flow)
- [Build](#build)
- [Runtime Commands](#runtime-commands)
- [Release Packages](#release-packages)
- [Repository Layout](#repository-layout)
- [TightVNC And Licensing](#tightvnc-and-licensing)

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
| `ConnectionStrings:MariaDb` | `CHANGEME` sample | Yes | MariaDB connection string. `deploy/setup-db.sh` writes this to `/etc/remoteserver/db.env`. |
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

The `deploy/` scripts generate the values that should not be committed:

| Script | Output |
|---|---|
| `setup-db.sh` | MariaDB database/user and `/etc/remoteserver/db.env`. |
| `deploy-server.sh` | `/opt/remoteserver`, command-signing key, client CA, `secret.key`, systemd unit, package directory. |
| `setup-bastion.sh` | Restricted `agent` SSH user, SSH CA key, sshd Match block, bastion host key output. |
| `setup-tls.sh` | Let's Encrypt certificate via Cloudflare DNS-01. |
| `setup-nginx.sh` | TLS reverse proxy, mTLS for `/agent` and `/api/*`, localhost-only `/admin/*`. |
| `fetch-tightvnc.sh` | Pinned TightVNC MSI and matching source ZIP under `third_party/tightvnc`. |
| `harden.sh` | UFW, fail2ban, and sshd hardening. |

---

## Deployment Flow

The detailed first-run guide is in [FIRST-RUN.md](FIRST-RUN.md). High-level order:

1. Build or download `RemoteServer-linux-x64.tar.gz`.
2. Run `deploy/setup-db.sh`.
3. Load `src/RemoteServer/Data/Migrations/schema.sql`.
4. Run `BASTION_HOST=remote.example.com deploy/deploy-server.sh`.
5. Set `Server__PublicUrl=https://remote.example.com` in `/etc/remoteserver/bastion.env`.
6. Run `deploy/setup-bastion.sh`.
7. Run `DOMAIN=remote.example.com ACME_EMAIL=admin@example.com deploy/setup-tls.sh`.
8. Run `DOMAIN=remote.example.com deploy/setup-nginx.sh`.
9. Run `deploy/harden.sh`.
10. Get the temporary first-admin password from `journalctl -u remoteserver`.
11. Run `RemoteServer mint-blob` on the server to create the first bootstrap blob.
12. Upload release packages for `agent`, `updater`, `client`, and optionally `vnc`.
13. Build an MSI from the console/admin API or enroll manually with `RemoteAgent.exe bootstrap "<blob>"`.
14. Approve the Pending device in the console.

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
| `RemoteServer-linux-x64.tar.gz` | Deploy to the Linux server with `deploy/deploy-server.sh`. |

Typical release:

```bash
git tag v1.5.11
git push origin v1.5.11
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

deploy/
  deploy-server.sh
  fetch-tightvnc.sh
  harden.sh
  setup-bastion.sh
  setup-db.sh
  setup-nginx.sh
  setup-tls.sh

icon/
third_party/
.github/workflows/build.yml
build.ps1
FIRST-RUN.md
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
