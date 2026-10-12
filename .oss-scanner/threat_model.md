# Threat model

Read with `README.md` (*Security Model*), `deploy/KEYS.md` (every key, its lifetime, what can be rotated) and the
ADRs in `docs/adr/`.

## What this project does and where untrusted input enters

RemoteAppClient is a remote-management system for fleets of Windows machines: a Windows service ("agent",
`src/RemoteAgent`, runs as SYSTEM on every managed device), an ASP.NET Core server (`src/RemoteServer`, Linux,
MariaDB, behind nginx), operator consoles (`src/RemoteClient` WinForms, `src/RemoteClient.Lite`,
`src/RemoteClient.Linux`), a read-only CLI (`src/RemoteClient.Cli`), an updater/supervisor service
(`src/RemoteAgent.Updater`), and shell deployment scripts (`deploy/`). Remote screen access is TightVNC over an SSH
reverse tunnel through a bastion (the same box as the server); files move over the same tunnel.

Untrusted input, in the order we care about it:

1. **The public server endpoints.** Everything under `/enroll/*` is reachable with no credential at all (an
   enrollment token in the body; `/enroll/rekey` is a lost-key request from a device that has nothing to
   authenticate with). `/auth/*` is the console sign-in (password + TOTP, Windows Hello, password recovery).
   `/api/*`, `/agent` (WebSocket command channel) and `/ssh` (SSH over WebSocket) are reachable only with a
   device client certificate from our own CA, enforced by nginx (mTLS); the server trusts nginx's identity
   headers only on requests that carry the proxy secret (`X-RAC-Proxy`). `/admin/*` is reachable only through an
   authenticated console session or a scoped API token, never from the public internet in the reference
   deployment.
2. **An enrolled device that turned hostile.** A device holds a valid client certificate and may send anything on
   `/api/*` and the command channel: telemetry (`DbTelemetrySink`), access results, re-key and tunnel-key
   requests, lost-key requests, file names. The server must never let one device affect another device's record,
   keys, tunnel port or sessions, and must never let a device elevate to an operator.
3. **The server as seen by the agent.** The agent treats the server as semi-trusted: commands must carry a valid
   ECDSA P-256 signature made with the enrolled server signing key (device-bound, nonce + time window); the
   server may choose only a remote bastion port for a tunnel; the bastion host key, user and local target are
   pinned on the device. Update packages are fetched from the server and installed by the updater; the server
   decides what to send, so a compromised server is a fleet compromise by design - but a *man in the middle* or
   a *spoofed* server must not be able to do anything (TLS pin, command signatures).
4. **Local users on a managed device.** Standard users must not reach the agent's data folder
   (`C:\ProgramData\RemoteAgent`: certificate thumbprints, the tunnel key's public half, VNC secret), the
   broker and status named pipes beyond what they are for, or the in-process ssh-agent pipe
   (`RemoteAgent.ssh-agent`, SYSTEM only). An administrator on the device is in the trusted computing base of
   that device: the device certificate's key and the tunnel key live in the TPM (CNG, non-exportable), so an
   administrator can use them while on the box but not take them with them. That distinction is the point of
   ADR-0003/0004.
5. **Operators.** A signed-in operator sees only the devices and groups they were granted; keyless/Lite
   operators can only view. Operator-to-operator and operator-to-admin escalation is in scope.
6. **The bastion.** Devices authenticate to sshd with CA-signed, short-lived SSH certificates under a restricted
   `agent` user (`deploy/steps/05-bastion.sh`); a revocation list is maintained by the server. A device must not
   be able to reach anything but its own loopback forwards.

## Components that matter most / least

- **Most:** `src/RemoteServer/Program.cs` (every endpoint), `Services/EnrollmentService.cs`,
  `Services/AuthService.cs` + the TOTP and session code, `Services/DbTelemetrySink.cs`, `Signing/*` (client CA,
  SSH CA, revocation list, command signing), the package upload and channel endpoints in `Program.cs`, the agent's
  `Update/UpdateInstaller.cs` and the updater (`src/RemoteAgent.Updater`), the agent's
  `Security/*` (command verification, key stores, data-folder ACL), `Services/CommandChannelService.cs`,
  `Services/SshCertService.cs`, `Services/SshAgentService.cs`, `Services/RekeyService.cs`, `Tunnel/*`,
  `Services/BrokerService.cs` (the console-side pipe on a device), and
  `deploy/` (the installer runs as root; `backup.sh`/`restore.sh` handle the fleet's secrets).
- **Less, still in scope:** the consoles (they hold session tokens and DPAPI-sealed trust tokens; UI code is not
  interesting, their HTTP/API client and local storage are), `RemoteClient.Core`.
- **Out of scope:** `src/RemoteAgent.Resources` (embedded third-party binaries: TightVNC), the WinForms layout
  and theming code, localization strings, `docs/`, `tests/` (test fixtures contain intentionally weak values).

## How to exercise it

- The unit tests (`tests/RemoteAppClient.Tests`, `dotnet test`) cover the pure parts: command signature
  canonical forms, token gates, the clock-skew tracker, the password hasher, the secret protector, the SSH
  certificate and key encodings, the re-key policy. They need no database or network.
- The server runs with `dotnet run --project src/RemoteServer` given a MariaDB and the configuration in
  `README.md` (*Server Configuration*); `src/RemoteServer/Data/Migrations/schema.sql` creates the schema.
  `deploy/setup.sh` is the reference deployment (Ubuntu, nginx, MariaDB, sshd bastion).
- The agent is Windows-only and runs as a service; most of its logic (verification, key handling, tunnel
  argument building, rotation state machine) is readable without running it.

## How you rate severity

- **Critical:** unauthenticated remote code execution on the server or on a device; any way for an
  unauthenticated party, or one enrolled device, to obtain another device's identity, open a tunnel to another
  device, execute a command on a device without a valid server signature, or obtain an operator/admin session;
  signature or mTLS bypass; a spoofed server being accepted by an agent; a malicious update package being
  installed.
- **High:** authenticated (operator) privilege escalation beyond granted devices/groups; leaking secrets at rest
  (VNC passwords, SMTP/Graph secrets, API tokens, backup contents) to anyone but an administrator; a standard
  local user on a device reaching the agent's keys, secrets or pipes; replay of commands or tokens; a device
  having another device's keys revoked (the revocation list is the point of attention after ADR-0004);
  SQL injection anywhere; path traversal in file transfer or package handling.
- **Medium:** denial of service that an unauthenticated party or a single device can cause to the whole server
  (not to itself); information disclosure of non-secret fleet data to the unauthenticated; weaknesses in rate
  limiting or lockouts; TOCTOU in the key/certificate rotation state machines that leaves a device without a
  working identity (availability, not confidentiality).
- **Low:** anything requiring an administrator on the device or root on the server; log noise; hardening
  suggestions.

## Anything to leave alone

- The constant log line in `VncProvisioningService` that static analysis flags as "cleartext storage of
  sensitive information" because the localization key contains the word *Password*: it logs a fixed sentence,
  never a secret (known false positive, repeatedly triaged).
- A local administrator or SYSTEM on a managed device can make the TPM sign; that is the accepted model
  (ADR-0003/0004), not a finding.
- The server administrator (root on the box) can read everything on the box, including the key files in
  `/etc/remoteserver`; that is the accepted model (`deploy/KEYS.md`).
- Third-party components embedded or installed (TightVNC, OpenSSH, nginx, MariaDB) are out of scope unless we
  use them wrongly.
