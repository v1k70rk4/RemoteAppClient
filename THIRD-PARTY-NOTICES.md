# Third-Party Notices

RemoteAppClient itself is released under the MIT License. Third-party
components retain their own licenses.

This product relies on third-party software that is **not** included in this
source repository but is fetched at build/package time and shipped alongside the
release artifacts.

## TightVNC

- **Component:** TightVNC Server (Windows)
- **Version:** 2.8.87
- **License:** GNU General Public License, version 2 (GPLv2)
- **Homepage:** https://www.tightvnc.com/
- **Binary (MSI):** https://www.tightvnc.com/download/2.8.87/tightvnc-2.8.87-gpl-setup-64bit.msi
- **Corresponding source:** https://www.tightvnc.com/download/2.8.87/tightvnc-2.8.87-src-gpl.zip

TightVNC is used as a **separate program**: the RemoteAgent installs it via its
official MSI (`msiexec`) and communicates with it only over the local VNC (RFB)
protocol and the Windows service interface. This is mere aggregation — it does
**not** make RemoteAppClient a derivative work of TightVNC, and RemoteAppClient's
own code is under its own license.

### GPLv2 compliance

When distributing a release that bundles the TightVNC binary, you must also make
the **corresponding source** available. `deploy/fetch-tightvnc.sh` downloads both
the exact MSI and the matching source archive (verified by SHA-256); ship the
source archive and a copy of the GPLv2 license (`COPYING`, found inside the
source archive) together with the release.

TightVNC is a trademark of GlavSoft LLC.
