---
title: Configuration
description: Every setting of the server, the agent and the consoles with its default and who sets it, and the shared per-device language setting.
---

# Configuration

Four configuration surfaces: the server's `appsettings.json` and environment, the agent's `appsettings.json` plus the files enrollment generates, the console's local settings, and one shared language setting per machine.

## Server

Settings are read from `appsettings.json` and environment variables; the deployment writes the sensitive values into `/etc/remoteserver/*.env`, which the systemd unit loads. Environment variables use ASP.NET Core's double-underscore form:

```bash
Server__PublicUrl=https://remote.example.com
Server__Bastion__Host=remote.example.com
ConnectionStrings__MariaDb="Server=localhost;Port=3306;Database=remoteserver;User Id=remoteserver;Password=..."
```

| Setting | Default | Required | Description |
|---|---:|:---:|---|
| `ConnectionStrings:MariaDb` | sample | Yes | The MariaDB connection string; `02-mariadb` writes it to `/etc/remoteserver/db.env`. |
| `Server:CommandSigningKeyPath` | `/etc/remoteserver/cmd_signing.key` | Yes | ECDSA P-256 private key that signs agent commands; the public key goes to the agents at enrollment. |
| `Server:CaCertPath` | `/etc/remoteserver/ca.crt` | Yes | The client-certificate CA's certificate; nginx uses it for mTLS too. |
| `Server:CaKeyPath` | `/etc/remoteserver/ca.key` | Yes | The CA's private key, readable by the service user only. |
| `Server:ClientCertValidityDays` | `825` | No | Validity of issued device certificates; they renew within 180 days of their end. |
| `Server:SecretKeyPath` | `/etc/remoteserver/secret.key` | Yes | 32-byte AES-GCM key for secrets in the database. |
| `Server:PackagesDir` | `/var/lib/remoteserver/packages` | Yes | The package store: uploaded executables and MSIs, generated installers. |
| `Server:PublicUrl` | empty | For blobs and MSIs | The public base URL embedded into bootstrap blobs, e.g. `https://remote.example.com`. |
| `Server:MinClientVersion` | `1.5.0.0` | No | The oldest console allowed to sign in; an older one is made to update first. Empty disables the gate. |
| `Server:MsiSigning:CertPath` | empty | No | Authenticode certificate for generated MSIs; empty means unsigned. |
| `Server:MsiSigning:Password` | empty | If signing | The PFX password; prefer the environment file. |
| `Server:MsiSigning:TimestampUrl` | `http://timestamp.digicert.com` | No | Timestamp server for `osslsigncode`. |
| `Server:Bastion:Host` | empty | Yes | The SSH bastion the agents are told at enrollment. |
| `Server:Bastion:Port` | `22` | No | Its SSH port; the agents try 443 first (the multiplexer) and fall back to this. |
| `Server:Bastion:User` | `agent` | No | The restricted SSH user. |
| `Server:Bastion:HostKey` | empty | Yes | The bastion's host key in `known_hosts` form, e.g. `ssh-ed25519 AAAA...`. |
| `Server:Bastion:SshCaKeyPath` | `/etc/remoteserver/agent_ca` | Yes | The SSH CA that signs device and operator SSH certificates. |
| `Server:Bastion:SshCertValidityDays` | `825` | No | Validity of device SSH certificates. |
| `Server:Bastion:AgentSshCertValidityDays` | `3` | No | Validity of the short SSH certificates the agents renew on their own. |
| `Server:Bastion:TunnelPortMin` / `TunnelPortMax` | `50000` / `60000` | No | The per-device reverse-tunnel port range; file ports follow above it. |

Stored in the database and edited in the console: the owner's name, support phone and e-mail; the e-mail provider (none, SMTP or Microsoft Graph) and its secret; users, roles and grants; devices and groups; packages and channels; tokens; the audit log; sessions.

## Agent

The agent reads `Agent` options from `appsettings.json` and the environment, then takes most runtime values from `C:\ProgramData\RemoteAgent\enrollment.json` after enrollment. In an MSI or bootstrap install nothing here needs editing.

| Setting | Default | Usually set by | Description |
|---|---:|---|---|
| `Agent:AgentId` | empty | enrollment | The device id the server assigned. |
| `Agent:EnrollmentDir` | `C:\ProgramData\RemoteAgent` | appsettings, MSI | Enrollment output and runtime state. |
| `Agent:ClientCertPfxPath` | empty | enrollment | The client certificate for mTLS. |
| `Agent:CommandChannel:Url` | sample | enrollment | The WebSocket endpoint, e.g. `wss://remote.example.com/agent`. |
| `Agent:CommandChannel:ServerCertPinSha256` | empty | enrollment | SHA-256 pin of the server's TLS certificate. |
| `Agent:CommandChannel:CommandSigningPublicKey` | empty | enrollment | The server's command-signing public key (SPKI, Base64). |
| `Agent:CommandChannel:MaxCommandAgeSeconds` | `60` | appsettings | The replay window for signed commands. |
| `Agent:CommandChannel:ReconnectBaseDelaySeconds` / `ReconnectMaxDelaySeconds` | `2` / `120` | appsettings | Reconnect backoff after the channel drops; reset on a change of address. |
| `Agent:CommandChannel:KeepAliveIntervalSeconds` / `KeepAliveTimeoutSeconds` | `15` / `10` | appsettings | WebSocket keepalive and the pong timeout. |
| `Agent:Tunnel:BastionHost` / `BastionPort` / `BastionUser` | sample / `22` / `agent` | enrollment | The bastion. |
| `Agent:Tunnel:BastionTransport` | `auto` | the console, per device | `auto` (443 raw SSH, then WebSocket), `ssl443`, `ssh22` or `wss443`. |
| `Agent:Tunnel:PrivateKeyPath` / `CertificatePath` | enrollment dir | enrollment | The tunnel key (a TPM key is served by the built-in ssh-agent instead) and its certificate. |
| `Agent:Tunnel:BastionHostKey` | empty | enrollment | The pinned host key. |
| `Agent:Tunnel:LocalForwardPort` | `5900` | appsettings | The local port the reverse tunnel exposes: TightVNC. |
| `Agent:Tunnel:IdleTimeoutSeconds` | `1800` | appsettings | Closes an idle tunnel. |
| `Agent:Tunnel:SshExecutablePath` | `C:\Windows\System32\OpenSSH\ssh.exe` | appsettings | `ssh.exe`; when it is not there, `Program Files\OpenSSH` and the PATH are tried. |
| `Agent:Telemetry:IngestUrl` | sample | enrollment | The telemetry endpoint, e.g. `https://remote.example.com/api/telemetry`. |
| `Agent:Telemetry:ServerCertPinSha256` | empty | enrollment | The TLS pin for the API calls. |
| `Agent:Telemetry:IntervalSeconds` | `60` | appsettings | The telemetry interval. |

`C:\ProgramData\RemoteAgent\diag.json`, written by the *verbose logging* command, raises the file log's level for the time it names; the service reads it without a restart.

## Console

The Windows console keeps its settings in `%APPDATA%\RemoteClient\config.json` (the Lite console in `%APPDATA%\RemoteClient.Lite`):

| Setting | Default | Description |
|---|---:|---|
| `AdminApiPort` | `5000` | The bastion loopback port of the admin API, which the local agent's broker forwards. |
| `ViewerExe` | `C:\Program Files\TightVNC\tvnviewer.exe` | The TightVNC viewer. |
| `ThemeMode` | `dark` | `light`, `dark` or `auto`. |
| `Channel` | `rtm` | The console's own update channel. |
| `HelloCredentialId`, `HelloUsername` | - | The Windows Hello credential registered on this machine. |

The viewer's scale and colour depth roam with the account; the language is the shared setting below.

## Shared language setting

Every local executable - agent, updater, console, Lite - reads one file:

```text
%ProgramData%\RemoteAppClient\settings.json
```

| Value | Meaning |
|---|---|
| `auto` | The system UI culture. |
| `en` | English. |
| `hu` | Hungarian. |

The console's settings page writes it; running services and open windows pick the change up at their next start.
