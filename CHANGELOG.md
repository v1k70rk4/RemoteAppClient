# Changelog

Release notes for RemoteAppClient, newest first. Each section is what the README's "What's New" said at
the time of that release; the GitHub release pages carry the same text together with the artifacts.

## What's New in 2.2.5

Fixes from a week of running the fleet: two timing bugs that made a healthy device look slow or silent, a false
clock alarm on sleeping laptops, and a small console convenience. Every component is **2.2.5.0**. There is **no
schema change**. The release also covers 2.2.3 and 2.2.4, which ran on the maintainer's fleet but were never tagged.

**Tunnels no longer wait out ssh's ConnectTimeout**
- Both the device's reverse tunnel and the operator's local forward passed `ConnectTimeout=8` to `ssh.exe`, so that a
  dead bastion port would fail fast. The OpenSSH 8.1 client that Windows 10 (and Server 2016/2019) ships sleeps
  through the whole timeout before it notices that the connection is up: on a Windows 10 workstation the connect
  phase took 8.0 s with the option and was immediate without it, on a link where a plain TCP connect takes 1 ms.
  Windows 11's newer client does not have the bug, which is why only some devices were slow.
- The option is gone. The reverse tunnel's 12-second authentication deadline and the local forward's 15-second
  port deadline bound a black-holed port instead, and a forward that is still not accepting at its deadline is
  given up so the next transport (WSS) gets its turn, where it used to be assumed ready.

**A device that answers fast is no longer "not answering"**
- The open-tunnel, message and power endpoints bound the requesting operator to the command's nonce only after the
  command had been delivered and its status saved. An agent that answered before that bookkeeping finished (a
  desktop on a fast link does so routinely) had its answer parked under a placeholder, which the binding then
  overwrote. The console polled the empty result for 17 seconds and reported "the device did not answer" while the
  tunnel was in fact open, and the audit row for the connect said "?" with no device.
- The binding now merges into the parked answer instead of replacing it, and the audit row is written by whichever
  side learns the full context last, so it always names the operator and the device.

**A tunnel start is safe from the idle watchdog, and says where its seconds go**
- The idle watchdog took a tunnel whose ssh had just been started for a running one, and with the activity stamp
  still at the previous session's end it closed it mid-start. The start then failed with `No process is associated
  with this object` until somebody restarted the agent's services. The stamp is now set before the start, and a
  process disposed by another task counts as gone rather than as a fault.
- The agent times a tunnel start from ssh's own `-v` milestones (launch, name resolution, connect, key exchange and
  authentication) and logs the breakdown as a warning when it took 3 seconds or more, so a device's event log says
  which phase to look at. The agent also no longer compares its clock to the server's from a reply that a sleep
  interrupted (a round trip over 10 seconds is not read).
- The console's status line after a connect shows two numbers: how long the device took to answer the command, and
  how long its tunnel plus VNC took after that. A slow second number points at the device, not at the network.

**A sleeping laptop is not a clock error**
- Telemetry carries the time it was collected, and the server compared that with its own clock to spot devices
  whose clock has drifted (past 60 seconds the agent discards every command). A laptop that dozed off between
  collecting a report and the request reaching the server delivered an old stamp when it woke, which looks exactly
  like a clock that is behind: laptops on Modern Standby got *clock 34 s behind* on every lid-close and stayed
  marked *Error* until they stayed awake long enough to report again.
- Delay can only make a stamp look older, never newer, so the server now reads a device's clock from the freshest
  stamp among its reports of the last five minutes. A clock that is ahead is flagged from one report, since no delay
  can fake that; a clock that is behind needs two reports that agree. *Problem since* restarts only when the kind of
  problem changes, not when the offset jitters by a second.

**Click a telemetry value to copy it**
- On the device's Telemetry tab and in the session side panel, clicking a value (hostname, addresses, make and
  model, serial number, device id, anything) copies it to the clipboard, with a short confirmation in place. Only
  the value text is the target, so a click that merely brings the window forward does not replace what was on the
  clipboard; a placeholder value is not copyable.
- The panel now refreshes in place instead of rebuilding its rows every 30 seconds, and the public IP and its
  reverse name are separate rows (in the Linux console too), so a click copies exactly what the row shows and a
  narrow panel no longer truncates the address behind a long hostname.

**Versions**
- Every component carries 2.2.5.0. The updater and the CLI carry the aligned version only.

## What's New in 2.2.2

Hardening that came out of a real fleet move: a server restored onto a new box, and a batch of new devices enrolling
over poor mobile links. Every component is **2.2.2.0**. There is **no schema change**. The release also covers
2.2.1, which was merged but never tagged.

**MSIs keep one ProductCode**
- The generated MSI declared `Product Id="*"`, so every build got a fresh ProductCode. Deployment tools (Intune, GPO,
  SCCM) detect an installation by its ProductCode: a rebuilt MSI looked like a different product, was pushed onto
  machines that already ran the agent, and the shared UpgradeCode turned that into a major upgrade, which uninstalls
  and re-enrolls the device.
- The ProductCode is now constant (`Server:MsiProductCode` overrides it for an estate that already deployed another
  code; an invalid value falls back to the default). The UpgradeCode is unchanged and wixl still mints a new
  PackageCode per build. Running a rebuilt MSI on a machine that already has the product is refused by Windows
  Installer (1638), which is the point: the MSI is the first installer, versions ship through the release channels.
- In Intune, set *Ignore app version* to *Yes* for the app, because the agent updates itself and its version moves on
  without the MSI.

**No silently smaller MSIs**
- The package directory is deliberately not part of the fleet backup (large, re-uploadable), so after a restore the
  package rows exist and the files may not. The MSI endpoint treated a missing updater, client or TightVNC file as
  "leave it out": it built an MSI without TightVNC, logged nothing, and the devices installed from it looked healthy
  and had no VNC password.
- A current package whose file is gone now refuses the build with `<component>_file_missing` plus the file name and
  logs a warning. Building without TightVNC because the channel has no vnc package at all stays possible, is logged,
  and the response carries `includesVnc`. The console's MSI panel names the file to upload again instead of showing
  a bare 404, and says so when an MSI was built without TightVNC.
- The diagnostics snapshot has a `packages` entry (current packages, missing files); the same check runs once at
  startup and logs a warning per missing file. `restore.sh --db` ends with a reminder to upload the agent, updater,
  client **and vnc** packages again.

**VNC provisions itself when TightVNC arrives**
- First-time VNC provisioning (per-device password, hardening, report to the server) ran once, at agent start. A
  device installed without TightVNC stayed without a VNC password even after a `vnc` rollout had installed it, until
  somebody restarted the service. On a flaky mobile link that means catching the device online twice.
- The 30-second watchdog now retries whenever there is no password yet and TightVNC is installed or the bundled MSI
  is present. A real failure (msiexec, registry) backs off, doubling up to half an hour; "waiting for a vnc rollout"
  is logged once.

**racctl**
- `diag` and `status` print the server's JSON verbatim instead of round-tripping it through racctl's own types, so a
  newer server's fields are never dropped by an older racctl.
- `get /admin/...` works from Git Bash, which rewrites a leading `/` into a Windows path.
- `devices` shows the TightVNC version and the reconnect count of the last hour.

**Also**
- The remaining unordered single-row settings queries are ordered; EF Core's *First without OrderBy* warning is gone.

**Upgrading**
- Server: upload `RemoteServer-linux-x64.tar.gz` and *Update server*. No SQL. Afterwards take a snapshot and check
  that `packages` lists nothing as missing, especially on a server that was restored from a backup.
- Agent: roll it out to get the VNC self-heal. Console: needed for the new MSI messages. Updater, Lite and the Linux
  console carry the aligned version only.
- Rebuild your MSIs after the server update so they carry the fixed ProductCode, and re-point deployment-tool
  detection rules at it once.

## What's New in 2.2.0

A release about **seeing and running the server without a shell on the box**. Every component is **2.2.0.0**.
There is a **schema change**: `upgrade-2.2.0-api-tokens.sql` adds one table and is idempotent.

**The server's own log, readable from the console**
- The server writes a daily-rolling log file next to its other state (`/var/lib/remoteserver/logs`, 14 days;
  `Server:LogDir` and `Server:LogRetentionDays` change that) and serves it over the admin session. *Server settings
  → Diagnostics* shows the newest records with level, time-window and text filters, with Copy and Save as. Until now
  the log lived only in journald, which the service user cannot read: on a box where nobody has root, nobody could
  read it.
- If the directory cannot be created, the server keeps the newest records in memory and says so in the snapshot.

**A health snapshot**
- One click, or `racctl diag`: version, uptime, memory and load, disks, database latency and table sizes, fleet
  counts (devices, connected, reporting, flaky, pending commands), the log's state, what the public name resolves
  to compared with the box's own addresses, the certificate the public 443 actually serves and its expiry, and the
  self-update state. Both mistakes of the last server move would have shown up in it within a minute.

**Access tokens for tooling**
- An admin mints tokens for their own account (*Diagnostics → Access tokens*). A token is shown once and only its
  hash is stored; it needs neither the password nor 2FA, and everything done with it is attributed to the admin,
  with the token's name in the audit detail. Revoke it any time; revoking a user's sessions revokes their tokens too.
- Two scopes. **Read-only**, the default, opens the log, the snapshot and the fleet listings and never a secret: a
  token never receives VNC passwords or device notes, and cannot reach the backup, MSIs, enrollment tokens or user
  management. **Read + server update** adds exactly the three self-update routes (stage, apply, roll back) and is
  deliberately loud in the UI, because a package the helper installs *is* the server.
- A token is accepted only on the tunnel-only `/admin` path: without an enrolled device's SSH access a leaked token
  reaches nothing. Ten rejected tokens from one address block token authentication for ten minutes.

**racctl, a command-line client**
- `src/RemoteClient.Cli` builds `racctl.exe`: `logs`, `diag`, `status`, `devices`, `events`, `audit`, `get`, and with
  an update token `update <tar.gz> [--sql upgrade.sql]`, `apply` and `rollback`, the same steps as the console's
  *Server update* tab, waiting for the helper's verdict. It reaches the server through the local agent's broker like
  the console does, so it works on an enrolled Windows device with the agent running, and keeps its token
  DPAPI-protected. It is not a release artifact: build it where you use it.

**A server stop takes a second, not thirty**
- Every server stop used to take the host's full 30-second shutdown timeout: the agents' command-channel sockets are
  long-lived requests that stay open, and the host waits for open requests. On stopping, the server now sends every
  agent a close frame and aborts whoever has not answered within three seconds. Measured with 11 connected agents,
  a self-update went from 37 s to 8 s, and because a clean close resets the agents' reconnect backoff, all of them
  were back within two seconds of the new server listening; they used to trickle back over one to two minutes.

**Also**
- The single-row settings query no longer trips EF Core's *First without OrderBy* warning.

**Upgrading**
- Server: upload `RemoteServer-linux-x64.tar.gz`, then `upgrade-2.2.0-api-tokens.sql` (the tar first: uploading a
  tar clears a previously staged SQL), then *Update server*.
- Console: 2.2.0 is needed for the Diagnostics tab and token management; older consoles keep working.
- Agent, updater, Lite and the Linux console: no functional change, the version is aligned.

## What's New in 2.1.8

A release about **how RemoteAppClient itself is built and shipped**: the Windows executables are **code-signed**, a
server installed with `setup.sh` identifies its devices again, and one script covers every build. There is **no
database schema change**. Component versions: server and Windows console **2.1.7.0**, everything else **2.1.6.0**.

**Signed releases**
- The Windows assets of a release — agent, updater, console and Lite — are signed with an **Open Source Developer
  code-signing certificate** and timestamped, so the signature stays valid after the certificate expires. Windows
  can now verify who published them: Smart App Control accepts a valid signature, and SmartScreen reputation builds
  up on the certificate instead of starting from zero with every new file.
- The key lives in the cloud and signing needs the maintainer present, so CI still builds unsigned. `build.ps1 -Tag`
  rebuilds the tagged commit, signs it, and prints the command that replaces the release's unsigned exes.

**A fresh server that tells its devices apart**
- The server identifies a device by its client certificate, whose name nginx forwards as `X-Client-Dn`.
  `deploy/steps/07-nginx.sh` set that header for the command channel but **not for `/api/`**, so a server installed
  with `setup.sh` filed the telemetry of every device under a single device called `unknown`, answered VNC password
  reports with 401, and showed every real machine as *reporting only*. Hand-built configurations were not affected;
  it surfaced when a server was restored onto a new machine.
- On an existing server installed with `setup.sh`: add `proxy_set_header X-Client-Dn $ssl_client_s_dn;` to the
  `location /api/` block of the site configuration, reload nginx, then delete the device whose Telemetry tab shows
  deviceId `unknown`. Devices sort themselves out within a minute.

**One build script for every job**
- `.\build.ps1` without parameters now explains itself: `-Fleet` (signed with the fleet certificate, optionally
  `-Deploy`), `-Tag` (release build, signed with the release certificate, never deployed), `-Msi` (signs the MSI the
  server generated), `-ServerOnly` (the Linux server package) and `-Unsigned` (development).
- **The server package can be built on Windows**: `RemoteServer-linux-x64.tar.gz`, ready for the console's *Server
  update* tab. It is packed with explicit Unix file modes — only the apphost and `createdump` are executable — because
  a mode guessed by a Windows tool only fails once it reaches the Linux box.
- Signing scripts and folders are machine-specific and live in `build.local.psd1` next to the script, which git
  ignores: the public script carries no accounts, certificates or paths.
- A build no longer stops this machine's agent services unless it is really about to overwrite them (`-Deploy`).

**Also**
- The server logs executed SQL at Debug instead of Information; the journal had become mostly SQL text.

---

## What's New in 2.1.7

A release for **cleaning up a fleet**: notes for hundreds of machines at once, and a badge that stops calling a
switched-off machine flaky. There is **no database schema change**. Component versions are not aligned this time:
the Windows console is **2.1.7.0**, everything else is **2.1.6.0**.

**Notes for hundreds of devices at once**
- **Import notes** (admin-only, the new button next to *Refresh*): open or paste a `hostname;note` list — a sheet
  saved from Excel as CSV, or two columns copied straight out of it — and see line by line what would happen before
  anything is written: *New*, *Overwrite*, *Not found*, *Invalid line*, *Repeated*, *Empty note*, *Unchanged*.
- It writes **only to devices already enrolled**. An unknown name is reported and skipped, never created, so the same
  list can be run again as more machines arrive — and *Copy unknown names* hands you exactly the ones still missing.
- **A name shared by several devices goes to the one seen most recently.** The others are usually stale
  re-enrollments, and leaving them without a note is precisely what makes them easy to find and delete.
- Nothing is lost by accident: a blank note never clears one, and **overwriting an existing note stays off** until
  you switch it on — the preview shows the current note next to the new one.
- Excel is taken as it comes: the Windows code page its plain CSV is written in (as well as UTF-8 and UTF-16), quoted
  cells, the trailing empty columns of a sheet that once had more, a header row, fully qualified names.
- The console sends **device IDs, not names**, so the server writes exactly the rows you approved. Every changed note
  gets its own audit entry (*Note imported*, filterable in the log); the note text itself stays out of the log, since
  notes are stored encrypted.

**Offline means offline**
- A machine switched off at the end of the day could still show as **flaky** half an hour later. Flaky means three or
  more reconnects within the last hour, and it was checked before offline — so a device that reconnected a few times
  on its way out kept the badge for the whole hour. Now a device that has **stopped reporting is offline**, whatever
  its link did before; *flaky* is kept for a machine that is still sending data over a bad connection. The server
  (which writes the device history) and the consoles decide it the same way, and the status column now sorts by it.

**Also**
- Long lists — audit log, users, groups, device history — no longer grow a stray horizontal scrollbar that hid the
  last row once the rows overflowed.
- `build.ps1 -SignScript <script>` signs every exe **before** it is hashed or deployed. Agents verify an update's
  hash, so signing afterwards would make every agent refuse it. Accounts and certificates stay in your own script,
  out of the repository, and a failed signature counts as a failed build.

---

## What's New in 2.1.5

A release about **saying what is wrong**. A device could be online, green and reporting every minute while
silently discarding every command sent to it — and nothing, anywhere, said so. That happened on a live
machine and cost an afternoon to find; this release makes the fleet admit it in under a minute, and then
fix itself. This release **changes the database schema**: two nullable columns are added to `Devices`. Prod
applies the idempotent `upgrade-2.1.5-device-problem.sql`; a fresh install gets them from `schema.sql`.

**A device that admits what is broken**
- New **error** state, with the reason: the console shows a red badge and the concrete fault rather than a
  reassuring green one. The first fault it knows is **clock skew**, because that is what bit us — an agent
  refuses any command whose timestamp is more than 60s from its own clock, so a machine running 88 seconds
  fast is completely unreachable while looking perfectly healthy.
- **The server detects it on its own, with no agent update.** It compares the telemetry's own
  `CollectedAtUtc` against arrival. Telemetry is not signed, so it still arrives from a device whose every
  command is being thrown away — which makes it the only channel that can report that fault at all. The
  warning fires at 30s, half the window, while there is still time to fix it.
- The problem is stored as a language-neutral code and rendered by each console in its own language; an
  unrecognised code is shown raw rather than hidden, so an older console cannot swallow a fault it has not
  learnt the name of yet. State changes land in the device history like any other.

**An agent that fixes its own clock**
- Time sync runs at startup, periodically, and — the part that matters — **whenever the clock is shown to be
  wrong**. Two independent signals trigger it: a command that carries a valid server signature but an
  out-of-window timestamp (proof the fault is ours, not a forgery), and the `Date` header on every telemetry
  response, which catches it within one 60-second cycle without any command needing to arrive.
- The clock is never taken from either signal — they only prompt the agent to consult a real time source.
  A domain-joined machine is left alone (its time comes from the DC), and an existing NTP configuration is
  never overwritten; only a machine with no source at all is given one.
- The correction is measured and logged: *"the clock was stepped by −180s"*. A system that quietly moves a
  machine's clock by three minutes should leave a record that it did.

**Fewer confident wrong answers**
- The console no longer puts up **"waiting for the user at the device to approve"** when no one was asked.
  It could not tell before: it only knows the device's own tri-state consent setting, while the effective
  value comes from group inheritance — so the server now returns it. When consent really was requested the
  wait is unchanged; when it was not, a silent device is reported as a silent device.
- **Show VNC password** (admin-only, right-click): the console hands the secret straight to the viewer and
  never displays it, so reading one previously meant decrypting the database by hand. Every read is written
  to the audit log — that record is the point, which is why it re-fetches rather than using the copy already
  in the device list.
- "nem vezérelhető" is now **"csak jelent"**, which fits the badge.
- `build.ps1 -Deploy` replaces the live install in one step: stops the services, kills the console, swaps the
  binaries while everything is down, verifies each copy by hash, and only then restarts.

---

## What's New in 2.1.0

An **honesty** release: the console stops asserting things it cannot know. `Online` now means a device is
genuinely reachable, a machine that only *looks* dead gets labelled instead of buried, and — for the first
time — you can ask a device where it has been. This release **changes the database schema**: the per-minute
telemetry snapshot is replaced by an event log. Prod applies the idempotent
`upgrade-2.1.0-device-events.sql` through the in-app server update; a fresh install gets the table from
`schema.sql`.

**Online that means online**
- The badge could stay lit for hours after a machine fell off the network. The server's WebSocket had a
  keepalive *interval* but no *timeout*, so a socket whose peer had vanished was never torn down — the
  connection registry kept a dead handle and reported it as connected. One device sat "Online" with a
  four-hour-old last report while six commands were "delivered" into that dead socket. Sockets now time out
  (`KeepAliveTimeout`), and the badge additionally cross-checks the last report, so a lingering handle
  cannot outlive the truth.
- Because of that, starting a session against an unreachable device raised a **consent prompt nobody could
  answer**. Commands now report whether they actually reached the device, and say so plainly when they did not.
- New state **"nem vezérelhető"** — telemetry is arriving but the command channel is not up. This used to
  render as plain *offline*, which is why a demonstrably alive machine could show up as dead. Liveness is
  now defined in exactly one place, so the badge, the filters and the history cannot drift apart.
- **Both consoles now answer the same question the same way.** The state decision moved into shared code, so
  the Windows list badge, the Windows detail panel and the Linux console can no longer disagree about one
  device — they used to: the detail panel still said *offline* while the badge next to it said *nem
  vezérelhető*, and the Linux console, which knew only online/offline, said *offline* for both. Failing to
  connect now names the actual state instead of claiming the machine is offline.
- **The agent's command loop can no longer die quietly.** An unhandled exception ended the background
  service for good: the device kept sending telemetry — so it looked perfectly healthy — but never accepted
  another command until someone restarted the service by hand. The loop now catches itself and retries
  every 60 s.

**A fleet with a past**
- A new **Előzmények** tab (also on the right-click menu): when a device changed state (online / ingadozó /
  nem vezérelhető / offline), and when its IP moved. The reverse DNS is resolved and stored **at write
  time** — once a device has moved on, nothing can recover the name a past address used to have.
- The old `DeviceTelemetry` table wrote every device's full payload once a minute. It reached **755 MB
  across fifteen devices** in three months, and no endpoint and no client had ever read a single row of it —
  every current value already lives denormalised on the device row. It is replaced by `DeviceEvents`, which
  records only transitions and is pruned at **90 days**; a device that stays put and stays online now writes
  nothing at all. The database went from **811 MB to 4 MB**.
- **Deleting a device no longer times out.** One machine had accumulated 63,574 snapshot rows — enough to
  push the cascade past the request limit. The event log removes the cause, and the delete clears it too.

**Smaller things**
- **Hozzáférési napló**: the log tab and its context-menu entry now say what they actually show.
- The public IP is shown with its reverse DNS beside it, in the list and in the history.
- Console broker reconnects are serialized, and a port forward always resolves the *current* broker rather
  than one disposed mid-reconnect.
- The **Linux operator console is fully localized** — its device list, status messages and settings panel used
  to stay English while the rest of the UI switched language. Two long-standing slips went with it: an uptime
  under an hour printed a hardcoded Hungarian word on the English UI, and the console was missing the link-
  quality row the Windows panel has.
- Dependency bumps (NuGet + Actions); the **Avalonia family is aligned at 12.1.2** across the Linux console.

---

## What's New in 2.0.0

A **survivability** release: the fleet now rides out the things that used to need a human — a network
blip, a lost report, an expired session — and, for the first time, an **OS swap under the server**.
**No database schema change since 1.9.0.**

**Surviving the server's own OS**
- `deploy/backup.sh` + `deploy/restore.sh` capture and re-adopt the **fleet's identity**: the CA that
  issued every client certificate, the command-signing key, the bastion SSH host key each agent pins, and
  the database. Devices are never "imported" — their identity lives on the device; restore these and an OS
  swap is invisible to all of them. Miss the SSH host key and every tunnel breaks: `04-server` rebuilds
  `bastion.env` from whatever key is present, so the restore goes **first**, and the installer (which only
  generates secrets that are *missing*) then adopts the fleet instead of locking it out.
- The same backup from the console: **Server settings → Backup**. A root helper does the privileged part
  (the server cannot read the host key), and the archive is **always passphrase-encrypted** — it leaves
  the box through an 8-hour admin session, and the keys inside cannot be revoked. The server keeps no copy
  of the passphrase and drops the archive as it hands it over.

**VNC that comes back on its own**
- **A network blip no longer kills VNC for ~6 minutes.** The bastion released a dropped session's reverse
  port only after a 120s×3 keepalive, while the agent gave up in 45s — so a returning agent could not
  rebind its own (deterministic) port and `ExitOnForwardFailure` killed the tunnel. The bastion now
  mirrors the agent (15×3), the agent **retries** across the window, and the console **waits for the RFB
  greeting** instead of launching a viewer at a tunnel that isn't there.
- The **VNC-secret report retries until the server confirms it**. A lost one-shot report used to leave a
  device with no server-side password until someone restarted the agent — the failure mode of fresh
  installs on mobile / CG-NAT links.

**Fewer dead ends**
- **Restart RACD** (Devices → Commands): restarts VNC → Helper → agent, in that order, and **verifies the
  Helper is alive before the agent goes down** — it is the only thing that can revive a stopped agent.
- An **expired session** now says so and returns to sign-in, instead of surfacing a raw `401`.
- Dependency bumps (NuGet + Actions, Avalonia 12.1) and a warning-free Linux console build.

---

## What's New in 1.9.0

A **client redesign and power-telemetry** release. This is the first release to **change the database schema
since 1.8.0**: four nullable power columns are added to `Devices`. Prod applies the idempotent
`upgrade-1.8.9-power.sql` (`ADD COLUMN IF NOT EXISTS`) through the in-app server update; a fresh install gets
the columns from `schema.sql`.

**Operator console redesign**
- A token-based visual overhaul of the Windows client: a compact, Hungarian-localized sidebar, tighter device
  stat cards, an icon-only refresh on the Devices page, and a polished deep-dark login screen.
- Bundled **IBM Plex** UI fonts (OFL), embedded in the single-file exe so they load without a system install.
- **Dark, click-to-sort list headers** throughout — the Devices list and the Channels/MSI device list sort on
  any column (version-aware), and the old white native ListView header is gone in dark mode.
- The Devices page **auto-refreshes** every 10 s, so "last seen" and online state stay live.

**Power telemetry (battery & sleep)**
- The agent reports **battery charge %**, **charger (AC) state**, and the configured **sleep timeout** on mains
  and on battery. Charger state is **event-driven** (a `GUID_ACDC_POWER_SOURCE` notification), so plugging or
  unplugging shows up on the next telemetry beat instead of waiting on a stale Session-0 poll.
- Opening a VNC session to a machine that may drop off now raises a **sleep warning** — on battery, or on mains
  with sleep still enabled — with the idle-to-sleep time. The telemetry panel gains **Akku/táp** and **Alvó mód**
  rows.

**Quality of life**
- Device notes accept **multi-line input** (Enter inserts a newline); one-line list and header previews collapse
  the newlines to spaces so wrapped notes no longer smear together.
- Dependency bumps (NuGet + GitHub Actions) via Dependabot.

**Compatibility** — the new power columns are nullable and the new telemetry fields are additive, so a 1.9.0
server stays compatible with older agents (they simply report no power data) and the 1.9.0 client renders older
devices with the power rows blank.

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
