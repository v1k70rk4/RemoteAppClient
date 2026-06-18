#!/usr/bin/env bash
# Build a .deb for the Linux operator console from a published, self-contained linux-x64 output.
#   build-deb.sh <publish-dir> <version> <icon-png> [out-dir]
# Runs on any Debian/Ubuntu with dpkg-dev (no .NET needed - it only packages the publish output).
set -euo pipefail

PUBLISH="${1:?usage: build-deb.sh <publish-dir> <version> <icon-png> [out-dir]}"
VERSION="${2:?version required}"
ICON="${3:?icon png required}"
OUT="${4:-.}"
HERE="$(cd "$(dirname "$0")" && pwd)"

command -v dpkg-deb >/dev/null || { echo "dpkg-deb not found (apt-get install dpkg-dev)"; exit 1; }
[ -x "$PUBLISH/RemoteClient.Linux" ] || { echo "publish dir is missing the RemoteClient.Linux binary: $PUBLISH"; exit 1; }
[ -f "$ICON" ] || { echo "icon not found: $ICON"; exit 1; }

STAGE="$(mktemp -d)"; trap 'rm -rf "$STAGE"' EXIT
install -d "$STAGE/opt/remoteclient" "$STAGE/usr/bin" \
          "$STAGE/usr/share/applications" \
          "$STAGE/usr/share/icons/hicolor/256x256/apps" "$STAGE/DEBIAN"

cp -a "$PUBLISH/." "$STAGE/opt/remoteclient/"
chmod 0755 "$STAGE/opt/remoteclient/RemoteClient.Linux"

# /usr/bin launcher
printf '#!/bin/sh\nexec /opt/remoteclient/RemoteClient.Linux "$@"\n' > "$STAGE/usr/bin/remoteclient"
chmod 0755 "$STAGE/usr/bin/remoteclient"

# desktop entry + icon
cp "$HERE/remoteclient.desktop" "$STAGE/usr/share/applications/remoteclient.desktop"
cp "$ICON" "$STAGE/usr/share/icons/hicolor/256x256/apps/remoteclient.png"

# control (fill in version + installed size)
INSTALLED_KB="$(du -sk "$STAGE/opt" "$STAGE/usr" | awk '{s+=$1} END {print s}')"
sed -e "s/@VERSION@/$VERSION/" -e "s/@INSTALLED@/$INSTALLED_KB/" "$HERE/control" > "$STAGE/DEBIAN/control"

mkdir -p "$OUT"
DEB="$OUT/remoteclient_${VERSION}_amd64.deb"
dpkg-deb --root-owner-group --build "$STAGE" "$DEB"
echo "built: $DEB"
