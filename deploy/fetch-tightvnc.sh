#!/usr/bin/env bash
# A hivatalos TightVNC (GPLv2) beszerzése PINNELT verzióval + SHA-256 ellenőrzéssel.
# A binárist NEM verziózzuk a repóban — ez a script reprodukálhatóan letölti.
#
# A letöltött MSI az agent release-csomagjába kerül (az agent csendben telepíti).
# A forrás-zip + a licenc a release-hez mellékelve → GPL-megfelelés. Mivel az agent
# külön folyamatként (msiexec) használja, a saját kód NEM lesz GPL (aggregation).
set -euo pipefail

VERSION="2.8.87"
OUT_DIR="${1:-third_party/tightvnc}"
BASE="https://www.tightvnc.com/download/${VERSION}"
MSI_NAME="tightvnc-${VERSION}-gpl-setup-64bit.msi"
SRC_NAME="tightvnc-${VERSION}-src-gpl.zip"

# Pinnelt SHA-256 (HTTPS-en letöltve, méret egyezik a hivatalos oldallal).
MSI_SHA256="aa256612c5b8bb387355e9c4bce6068bf9ba77ef849f54efcf6087d86b86f52a"
SRC_SHA256="8231a92295122df6b39406f512152ccde319ace9d91364c93788d0bfc91cc4b8"

mkdir -p "$OUT_DIR"
echo "[tightvnc] letöltés: TightVNC $VERSION"
curl -fsSL -o "$OUT_DIR/tightvnc.msi"     "$BASE/$MSI_NAME"
curl -fsSL -o "$OUT_DIR/tightvnc-src.zip" "$BASE/$SRC_NAME"

verify() {
  local file="$1" expected="$2" actual
  actual="$(sha256sum "$file" | awk '{print $1}')"
  if [ "$actual" != "$expected" ]; then
    echo "HIBA: $(basename "$file") SHA-256 nem egyezik!" >&2
    echo "  várt:   $expected" >&2
    echo "  kapott: $actual" >&2
    exit 1
  fi
  echo "[tightvnc] OK: $(basename "$file")"
}

verify "$OUT_DIR/tightvnc.msi"     "$MSI_SHA256"
verify "$OUT_DIR/tightvnc-src.zip" "$SRC_SHA256"
echo "[tightvnc] kész → $OUT_DIR"
echo "  - tightvnc.msi      → az agent release-csomagjába (csendes telepítés)"
echo "  - tightvnc-src.zip  → a release mellé (GPL: corresponding source)"
