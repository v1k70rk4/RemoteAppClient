---
title: Diagnostics
description: The server's log and health snapshot without a shell on the box, verbose logging on a device for a day, health alerts by e-mail, and what the console tells when a device does not answer.
---

# Diagnostics

## The server, without a shell

The server keeps its own daily log file and a health snapshot: version, uptime, memory, load, disk space of the directories it writes, the database's size and tables, the bastion's state, the update helper's state. Both are readable from the console (*Server settings → Diagnostics*) and from `racctl`:

```powershell
racctl logs --level warn --since 2h
racctl logs --day 2026-11-04 --grep <deviceId>
racctl diag
racctl status
```

The log names every command delivered, every access result with its nonce, every session password decision, every tunnel bridged over WebSocket, and every alert. A read-only API token, minted per administrator in the console and valid only through the tunnel of an enrolled device, is all `racctl` needs.

## Health alerts

A watcher checks the server's own health every minute - disk, database, the bastion script's format against what the server writes, certificate expiry, a device whose sign-in got locked - and mails the support address when a check starts failing, once, and again when it recovers. The checks are listed on the diagnostics page with their last state.

## Verbose logging on a device

A device that misbehaves is put into **verbose logging for 24 hours** from its menu: the agent's file log (`C:\ProgramData\RemoteAgent\logs`) goes to debug level without a restart, TightVNC writes its own detailed log (`C:\ProgramData\TightVNC`), and both are downloadable with the file manager. The level falls back by itself when the day is over.

The agent's event log (Windows *Application* log, source `RemoteAgent`) carries the warnings at all times: tunnel starts with their timing split (launch, resolve, connect, authenticate), ssh's own messages, the VNC watchdog's findings, the updater's incidents.

## When a device does not answer

The console does not guess. A request that the device never received is reported as such; a device that was asked and did not answer, as that; a locally locked device, as locked. A known fault on the device - the updater restarting a dead agent, a lost TPM key, a package that does not take - shows in the list as **Error** with the text of the problem, so an operator reads the reason instead of a timeout.

## Device history

Every device has a 90-day history of its state changes and address changes, sampled from its telemetry: when it went offline, when its public address changed, when it was reporting without a command channel. It is in the device's properties and in `racctl events <deviceId>`.
