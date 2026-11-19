#!/usr/bin/env bash
# Fetches official TightVNC (GPLv2) with a pinned version and SHA-256 verification.
# The binary is not versioned in the repo; this script downloads it reproducibly.
#
# The downloaded MSI goes into the agent release package and is installed silently.
# The source zip plus license are shipped with the release for GPL compliance.
# Since the agent uses it as a separate process (msiexec), the own code is aggregation.
set -euo pipefail

VERSION="2.8.90"
OUT_DIR="${1:-third_party/tightvnc}"
BASE="https://www.tightvnc.com/download/${VERSION}"
MSI_NAME="tightvnc-${VERSION}-gpl-setup-64bit.msi"
SRC_NAME="tightvnc-${VERSION}-src-gpl.zip"

# Pinned SHA-256 (downloaded over HTTPS; size matches the official site).
MSI_SHA256="9693cb4cdc2e7e482e8e3f5890415b57ef87de3369fa5c337db516218a1170ce"
SRC_SHA256="f3f76393a5865e1f1919c78c3eb43b9c937cf738927c2bfc920ecbd3913b061c"

mkdir -p "$OUT_DIR"
echo "[tightvnc] downloading: TightVNC $VERSION"
curl -fsSL -o "$OUT_DIR/tightvnc.msi"     "$BASE/$MSI_NAME"
curl -fsSL -o "$OUT_DIR/tightvnc-src.zip" "$BASE/$SRC_NAME"

verify() {
  local file="$1" expected="$2" actual
  actual="$(sha256sum "$file" | awk '{print $1}')"
  if [ "$actual" != "$expected" ]; then
    echo "ERROR: $(basename "$file") SHA-256 mismatch!" >&2
    echo "  expected: $expected" >&2
    echo "  actual:   $actual" >&2
    exit 1
  fi
  echo "[tightvnc] OK: $(basename "$file")"
}

verify "$OUT_DIR/tightvnc.msi"     "$MSI_SHA256"
verify "$OUT_DIR/tightvnc-src.zip" "$SRC_SHA256"
echo "[tightvnc] done -> $OUT_DIR"
echo "  - tightvnc.msi      -> agent release package (silent install)"
echo "  - tightvnc-src.zip  -> ship with the release (GPL: corresponding source)"
