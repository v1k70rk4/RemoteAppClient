#!/usr/bin/env bash
# Build an AppImage (one file, runs on any distro) from a published self-contained linux-x64 output.
#   build-appimage.sh <publish-dir> <version> <icon-png> [out-dir]
# Set APPIMAGETOOL to a local appimagetool binary to skip the download.
set -euo pipefail

PUBLISH="${1:?usage: build-appimage.sh <publish-dir> <version> <icon-png> [out-dir]}"
VERSION="${2:?version required}"
ICON="${3:?icon png required}"
OUT="${4:-.}"
[ -f "$PUBLISH/RemoteClient.Linux" ] || { echo "publish dir is missing the RemoteClient.Linux binary: $PUBLISH"; exit 1; }
[ -f "$ICON" ] || { echo "icon not found: $ICON"; exit 1; }

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT

TOOL="${APPIMAGETOOL:-}"
if [ -z "$TOOL" ]; then
  TOOL="$WORK/appimagetool"
  curl -fsSL -o "$TOOL" \
    "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
  chmod +x "$TOOL"
fi

APPDIR="$WORK/RemoteClient.AppDir"
mkdir -p "$APPDIR/usr/bin"
cp -a "$PUBLISH/." "$APPDIR/usr/bin/"
chmod 0755 "$APPDIR/usr/bin/RemoteClient.Linux"
cp "$ICON" "$APPDIR/remoteclient.png"

cat > "$APPDIR/remoteclient.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Multiserver Linux RemoteAppClient Lite
Comment=Operator console for RemoteAppClient (viewer-only)
Exec=RemoteClient.Linux
Icon=remoteclient
Terminal=false
Categories=Network;
EOF

cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/RemoteClient.Linux" "$@"
EOF
chmod 0755 "$APPDIR/AppRun"

mkdir -p "$OUT"
OUTFILE="$OUT/RemoteClient-${VERSION}-x86_64.AppImage"
# --appimage-extract-and-run lets appimagetool work without FUSE (e.g. headless/CI).
ARCH=x86_64 "$TOOL" --appimage-extract-and-run "$APPDIR" "$OUTFILE"
echo "built: $OUTFILE"
