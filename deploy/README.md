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

`fetch-tightvnc.sh` is separate: it downloads the pinned TightVNC MSI and the matching GPL
source ZIP into `third_party/tightvnc` (see the main README's TightVNC section).
