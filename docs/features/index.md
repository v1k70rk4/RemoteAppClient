---
title: Features
description: What RemoteAppClient does - remote access and file transfer, fleet management, updates and installers, diagnostics.
---

# Features

<div class="grid cards" markdown>

-   :material-remote-desktop:{ .lg .middle } **Remote access**

    ---

    The screen through a reverse SSH tunnel and TightVNC, a password chosen per session, consent or unattended per group, file transfer, messages and power actions, locks set at the device.

    [:octicons-arrow-right-24: Remote access](remote-access.md)

-   :material-view-list:{ .lg .middle } **Fleet management**

    ---

    The device list with state, user, network and power, groups with inherited policy, users, roles and grants, the audit log, device history, bulk notes.

    [:octicons-arrow-right-24: Fleet management](fleet.md)

-   :material-update:{ .lg .middle } **Updates and packages**

    ---

    Channels and components, rollouts the agent applies one package at a time with a verified hash, MSI installers per group, the server updating itself from the console.

    [:octicons-arrow-right-24: Updates and packages](updates.md)

-   :material-stethoscope:{ .lg .middle } **Diagnostics**

    ---

    The server's log and health from the console or `racctl`, verbose logging on a device for a day, health alerts by e-mail, what the console shows when a device is not reachable.

    [:octicons-arrow-right-24: Diagnostics](diagnostics.md)

</div>

## In one list

**Remote access**

- Lists enrolled devices with online state, user, local and public IP, component versions, boot time, VPN and Wi-Fi state, battery and charger, local lock state, and recent incidents.
- Opens a reverse SSH tunnel from the agent to the bastion and launches TightVNC viewer through that bastion port; a second operator joins a running session.
- Consent prompt to the signed-in user, or unattended, per group; a local lock on the device that nothing remote can undo.
- A two-pane file manager over the same tunnel.
- Availability questions and operator messages to the signed-in user; restart, forced restart, cancel, sign out; "connect as soon as it wakes" for a sleeping laptop.

**Fleet management**

- Device groups with inherited access policy; administrators and operators with device and group grants.
- Users, password reset and recovery, TOTP, optional Windows Hello sign-in; the Linux console's keyless operators.
- Audit log of access, enrollment, package, user, settings and security events; a 90-day history per device.
- Server branding: owner name, support phone and e-mail, shown on the device's prompts.

**Operations**

- Release channels `rtm` and `beta`; components `agent`, `updater`, `client` and `vnc`.
- Upload packages, roll out by channel, promote beta to RTM; the agent applies a rollout in order and verifies every hash.
- MSI installers built on the server per group, with agent, updater, optional console and Start-menu shortcut, the bundled TightVNC MSI and an embedded bootstrap blob.
- The server updates itself from the console, with backup and rollback.
- English and Hungarian throughout, one language setting per device for every local executable.
