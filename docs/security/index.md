---
title: Security model
description: How RemoteAppClient keeps command authenticity mandatory - device certificates in the TPM, pinned server certificate, signed and device-bound commands, a bastion that lets a device listen only while an operator asks, session passwords, Argon2id and encrypted secrets.
---

# Security model

RemoteAppClient treats command authenticity as mandatory: a device acts on a command only when it can prove the server wrote it, for this device, just now.

## Identity

- **Every device has its own client certificate**, issued at enrollment and used for mTLS on every connection to the server. Its private key is a non-exportable **TPM key** (Microsoft Platform Crypto Provider) wherever a TPM 2.0 is ready; software key storage is the fallback, and the console shows which.
- **The tunnel's SSH key is a TPM key as well**, served to `ssh.exe` by an ssh-agent built into the agent service, with a certificate signed by the server's SSH CA.
- **The agent pins the server**: the TLS certificate's fingerprint, the command-signing public key and the bastion's host key are written at enrollment and checked on every use. The bastion's host key goes into a private `known_hosts` created per SSH session.
- Certificates **renew by themselves** within 180 days of their end. A TPM cleared by a BIOS update becomes a **re-key request** in the console: one click issues the device a new key and certificate.
- Enrollment tokens are stored as hashes; the raw token is shown once. Devices enrolled with a bootstrap or MSI token are **pending** until an administrator approves them. Enrollment files are not trusted by their names: the service's data directory is cleaned of files owned by anyone but SYSTEM and Administrators.

## Commands

- The server signs every command with **ECDSA P-256**; the agent verifies it against the key it pinned at enrollment.
- Signatures are **device-bound**: the device id is part of what is signed, so a command cannot be replayed at a device it was not meant for. Replay at the same device is limited by a nonce the agent remembers and a timestamp window.
- The server can choose only the remote bastion port of a tunnel. The bastion host, user, key, certificate, host key and local target port come from the device's own configuration.
- An **update** names the package's SHA-256; the agent verifies the download before it stages anything.

## The tunnel and the bastion

- Agents only ever **dial out**: the WebSocket, the telemetry and the SSH tunnel all go to the server's 443. Managed devices need no inbound ports, and the VNC server stays on the device's loopback, reachable only through the reverse tunnel the agent opened.
- The bastion's `agent` user is restricted to port forwarding, and **a device may listen only while an operator is asking**: the server writes a grant (device, VNC port, file port, two minutes) when it delivers an open-tunnel command, and sshd's `AuthorizedPrincipalsCommand` lets that device bind those ports and no others. Every operator certificate may not listen at all. Rolled out in observe mode first, then enforced.
- A **file session** forwards the file port alone, so a password from an earlier screen session finds no VNC behind it.
- The operator's console holds **no key to the bastion**. The Windows console goes through the local agent's broker, which opens the forward with the device's identity and only for the ports the server allows; the Lite and Linux consoles get a 12-hour certificate at sign-in for a key they throw away.

## Sessions

- The **VNC password belongs to the session**: chosen by the server at open-tunnel, set by the agent before the tunnel comes up, handed to the operator only once the device has let the request in. It is kept for an hour and not rotated under a running session.
- **Consent** is the one policy the agent evaluates: with it required, nothing opens without the signed-in user's *Yes*. A lock set at the device cannot be undone remotely.
- Operators see only the devices and groups they were granted and can open tunnels only for those; administrators see everything. Every action is audited for a year.

## Operators and the server

- Console sign-in is username and password plus **TOTP**, or **Windows Hello** registered for the account on that machine. Passwords are hashed with **Argon2id**. A device's sign-in is locked for an hour after five bad passwords; keyless sign-ins are locked by source address.
- Database secrets - session passwords, the SMTP password, the Graph client secret - are encrypted at rest with **AES-256-GCM** under a server-side key file.
- `/admin/*` is **localhost only** on the server; nginx never exposes it. The console reaches it through a tunnel from an enrolled device, and `racctl` through the same tunnel with a read-only token.
- The server runs as its own user under a hardened systemd unit; the update helper that needs root is a separate path-unit-driven script that reads a package from a drop directory and never takes input from the service.

## What to keep safe

The keys and certificates on the server and on the devices, what each protects, which can be rotated and which expire: [Keys and certificates](keys.md). The threat model and the known limits: [`threat_model.md`](https://github.com/v1k70rk4/RemoteAppClient/blob/master/.oss-scanner/threat_model.md) in the repository.

!!! warning "Deployment assumption"
    Kestrel listens on localhost behind nginx. Do not expose the server's own port to the internet, and keep the server's identity archive (`deploy/backup.sh`) encrypted and off the box: it holds the private keys of the whole fleet.
