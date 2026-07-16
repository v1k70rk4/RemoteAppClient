# deploy — from-scratch RemoteServer setup

One **local** installer that takes a fresh Ubuntu box to a working `RemoteServer`:
database → server → bastion → TLS → 443 mux → hardening → self-update → first blob → verify.

Run it **on the server**, as a user with **passwordless sudo** (no SSH driving from your laptop):

```bash
./deploy/setup.sh                       # full run, asks what it needs
./deploy/setup.sh 06-tls 07-nginx       # a subset, in order
./deploy/setup.sh 11-verify             # just re-verify
```

Unattended: `cp deploy/config.env.example deploy/config.env`, fill it in, then run.

The full guide — prerequisites, the MariaDB choice, the step-by-step table, Cloudflare DNS-01,
first device enrollment, and troubleshooting — lives in the main
[README → Deployment Flow](../README.md#deployment-flow).

## Replacing the OS underneath (or moving to another box)

Devices are never "imported" — an agent's identity lives on the device (`enrollment.json` + its client
certificate). What the agent **pins** is what has to survive on the server:

| Agent pins | Kept alive by | Lose it and… |
|---|---|---|
| `commandSigningPublicKey` | `cmd_signing.key` | the fleet is visible but **unmanageable** — every command is rejected |
| `bastionHostKey` | `/etc/ssh/ssh_host_ed25519_key` | **no tunnels** — VNC and file transfer are dead |
| its own client certificate | `ca.key` + `ca.crt` | **nothing connects** — every device needs re-enrolling |

That is ~2 KB of secrets plus one SSH host key. Keep them and the swap is invisible to the fleet:

```bash
./deploy/backup.sh                                   # on the OLD box -> racd-identity-<ts>.tar.gz
#   ... rebuild the box, keep the same DNS name ...
./deploy/restore.sh racd-identity-<ts>.tar.gz        # 1. secrets + bastion host key
./deploy/setup.sh                                    # 2. finds them and REUSES them
./deploy/restore.sh racd-identity-<ts>.tar.gz --db   # 3. database; agents then reconnect on their own
```

The order is the whole trick: `04-server` only generates secrets that are **missing**, and it rebuilds
`bastion.env` from whichever SSH host key is present *at that moment*. Restore first and the installer
adopts the fleet; run it first and it mints new keys that lock every device out.

`db.env` is deliberately **not** restored (it is a local password, and its presence makes `02-mariadb`
skip installing MariaDB entirely). The archive holds the private keys of the whole fleet — store it
encrypted, off the box.

`fetch-tightvnc.sh` is separate: it downloads the pinned TightVNC MSI and the matching GPL
source ZIP into `third_party/tightvnc` (see the main README's TightVNC section).
