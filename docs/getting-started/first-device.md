---
title: The first device
description: Enroll the first Windows machine by hand with the bootstrap blob, install the agent service, approve the device, and sign in for the first time.
---

# The first device

For the first machine, manual enrollment is simplest; later devices get an [MSI built per group](../features/updates.md#msi-installers) from the console.

## Enroll and install

Put the release files in one folder on the device - `RemoteAgent.exe`, `RemoteAgent.Updater.exe`, and the pinned TightVNC MSI at `.\vnc\tightvnc.msi` if you want the agent to install TightVNC on first start - and run, in an **elevated** PowerShell:

```powershell
.\RemoteAgent.exe bootstrap "<blob>"
.\RemoteAgent.exe install-service --owner "Company Name" --group "First devices"
```

The blob is the one `09-blob` printed on the server (the console mints more under *Bootstrap*). What happens then:

1. The agent generates its device key - in the TPM where there is one - and sends a certificate request to the server with the enrollment token.
2. The server issues the device's client certificate and SSH certificate, and returns its command-signing public key and the bastion's host key, which the agent **pins** from then on.
3. The service starts, opens its WebSocket to the server, reports its first telemetry and provisions TightVNC when the MSI is there.
4. Because a bootstrap token is not auto-approved, the device waits as <span class="pill pending">PENDING</span> until an administrator approves it.

!!! info "Windows Server 2016"
    The agent drives the OpenSSH client (`ssh.exe`) for its tunnels. Windows 10 and later ship it; Server 2016 does not. Install [Microsoft's OpenSSH](https://github.com/PowerShell/Win32-OpenSSH/releases) (`Program Files\OpenSSH`) and the agent finds it without a restart. Without it the device is online and answers, but no tunnel comes up.

## First sign-in

Start `RemoteClient.exe` on an enrolled machine (the first device itself is fine) and sign in as `admin` with the temporary password. The console asks you to change it and to set up TOTP; from then on sign-in is password and code, or Windows Hello once registered.

Approve the pending device in the *Devices* view. From there you can upload packages, build group MSI installers, and use the normal remote workflow: pick the device, **Connect**.

## What the agent keeps on the device

Everything lives under `C:\ProgramData\RemoteAgent`, readable by SYSTEM and Administrators only:

| File | Purpose |
|---|---|
| `enrollment.json` | Device id, server URL, the server's command-signing public key, the bastion host, user and host key. |
| `agent.pfx.dat` | The client certificate for mTLS; the private key is in the TPM (software key storage as the fallback). |
| `ca.crt` | The server's client CA. |
| `id_ecdsa.pub`, `id_ecdsa-cert.pub` | The tunnel's SSH key (also a TPM key, served to `ssh.exe` by an ssh-agent built into the service) and its certificate signed by the server's SSH CA. |
| `vnc.secret` | The VNC password currently set on TightVNC. |
| `logs\` | The agent's daily log, 14 days. |

The device key never leaves the TPM. A BIOS update that clears the TPM turns into a one-click re-key request in the console; see [Security](../security/index.md#identity).

## Locks set at the device

Two things only someone at the device can change, and nothing remote can undo:

```powershell
RemoteAgent.exe vnc-lock       # no remote screen, however the server is configured
RemoteAgent.exe vnc-unlock
```

The file-transfer lock works the same way and ends a transfer in flight. Both show in the console's device list.
