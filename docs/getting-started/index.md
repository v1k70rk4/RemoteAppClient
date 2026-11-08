---
title: Get started
description: What you need for RemoteAppClient, and the three steps from an empty Linux box to the first remote session.
---

# Get started

Three steps, in this order: the server, the first device, the console. Half an hour on a fresh box.

<div class="grid cards" markdown>

-   :material-server:{ .lg .middle } **1. The server**

    ---

    A fresh Ubuntu box with a public DNS name. One script installs MariaDB, the server, the SSH bastion, TLS, the 443 multiplexer and the hardening, and prints the first bootstrap blob and admin login.

    [:octicons-arrow-right-24: Install the server](server.md)

-   :material-laptop:{ .lg .middle } **2. The first device**

    ---

    Two commands in an elevated PowerShell enroll a Windows machine and install the agent service. It turns up in the fleet as *pending*; you approve it from the console. Later devices get an MSI built per group.

    [:octicons-arrow-right-24: Enroll a device](first-device.md)

-   :material-monitor:{ .lg .middle } **3. The consoles**

    ---

    The Windows console runs on an enrolled machine and reaches the server through the local agent. The Lite and Linux consoles sign in with a short-lived certificate instead. `racctl` is the command line.

    [:octicons-arrow-right-24: Set up a console](consoles.md)

</div>

## What you need

| | |
|---|---|
| **Server** | A Linux box (the installer targets Ubuntu), a public DNS name pointing at it, port 443 open. A Cloudflare-managed zone makes TLS automatic (DNS-01); any certificate works otherwise. MariaDB is installed by the script or you bring your own. |
| **Devices** | Windows 10 or 11 (Windows Server works too). The agent runs as a SYSTEM service and needs the OpenSSH client: Windows 10 and later ship it; on Windows Server 2016 install [Microsoft's OpenSSH](https://github.com/PowerShell/Win32-OpenSSH/releases) first. A TPM 2.0 keeps the device key non-exportable; without one the key is software-protected. |
| **Operators** | The Windows console runs on a device that is itself enrolled (its agent brokers the tunnel). The Lite console and the Linux console need no agent; the Linux one needs `openssh-client` and `ssvnc` or `tigervnc-viewer`. |
| **Downloads** | The [latest release](https://github.com/v1k70rk4/RemoteAppClient/releases/latest): `RemoteAgent.exe`, `RemoteAgent.Updater.exe`, `RemoteClient.exe`, `RemoteClient.Lite.exe`, the server `tar.gz` and the Linux console `.deb`, with `SHA256SUMS`. The Windows executables are signed. |
| **TightVNC** | Not bundled. `deploy/fetch-tightvnc.sh` downloads the pinned MSI (and its GPL source) with a verified hash; the agent installs it on first start when the MSI sits next to it, and the server bundles it into the group MSIs. |

!!! tip "Already running an older version?"
    Every release page has an *Upgrading* section with the order: server first, then consoles, then agents. The current release is the supported one; the site describes it.
