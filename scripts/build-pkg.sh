#!/bin/bash
# build-pkg.sh -- builds the Cider .pkg installer: pkgbuild -> productbuild (optionally signed).
#
# Scope: the binary only, installed to /usr/local/bin/cider. This does NOT
# install the launchd agent -- `cider install` already does that, deliberately and reversibly,
# and a package that silently registers a background daemon is exactly the behaviour this
# project avoids. Do not add payload beyond usr/local/bin/cider to this script without updating
# that comment and the ticket it references.
#
# Usage:
#   scripts/build-pkg.sh <path-to-signed-cider-binary> <version> <output-pkg-path> [identity] [keychain-path]
#
# <identity> and <keychain-path> are optional. When omitted, productbuild produces an UNSIGNED
# product archive -- fine for local payload-layout testing (proving the binary lands at
# /usr/local/bin/cider inside the pkg), never fine for a real release. The release workflow
# always passes a Developer ID Installer identity when the .pkg job runs at all (it is guarded
# to only run when that cert secret exists -- see .github/workflows/release.yml).
#
# This script does not notarize or staple; the caller (CI or a human) does that afterward with
# `xcrun notarytool submit` / `xcrun stapler staple`, since those need account credentials this
# script has no business holding.
set -euo pipefail

BINARY_PATH="${1:?usage: build-pkg.sh <binary> <version> <out-pkg> [identity] [keychain-path]}"
VERSION="${2:?usage: build-pkg.sh <binary> <version> <out-pkg> [identity] [keychain-path]}"
OUT_PKG="${3:?usage: build-pkg.sh <binary> <version> <out-pkg> [identity] [keychain-path]}"
IDENTITY="${4:-}"
KEYCHAIN_PATH="${5:-}"

IDENTIFIER="com.chillicream.cider"

if [[ ! -f "$BINARY_PATH" ]]; then
  echo "::error::build-pkg.sh: binary not found at $BINARY_PATH" >&2
  exit 1
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

PAYLOAD_DIR="$WORK/payload"
mkdir -p "$PAYLOAD_DIR/usr/local/bin"
cp "$BINARY_PATH" "$PAYLOAD_DIR/usr/local/bin/cider"
chmod 755 "$PAYLOAD_DIR/usr/local/bin/cider"

COMPONENT_PKG="$WORK/component.pkg"
pkgbuild \
  --root "$PAYLOAD_DIR" \
  --identifier "$IDENTIFIER" \
  --version "$VERSION" \
  --install-location / \
  "$COMPONENT_PKG"

DISTRIBUTION_XML="$WORK/distribution.xml"
cat > "$DISTRIBUTION_XML" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="1">
    <title>Cider</title>
    <options customize="never" require-scripts="false" hostArchitectures="arm64"/>
    <domains enable_localSystem="true"/>
    <pkg-ref id="$IDENTIFIER"/>
    <choices-outline>
        <line choice="default">
            <line choice="$IDENTIFIER"/>
        </line>
    </choices-outline>
    <choice id="default"/>
    <choice id="$IDENTIFIER" visible="false">
        <pkg-ref id="$IDENTIFIER"/>
    </choice>
    <pkg-ref id="$IDENTIFIER" version="$VERSION" onConclusion="none">component.pkg</pkg-ref>
</installer-gui-script>
EOF

PRODUCTBUILD_ARGS=(--distribution "$DISTRIBUTION_XML" --package-path "$WORK")
if [[ -n "$IDENTITY" ]]; then
  PRODUCTBUILD_ARGS+=(--sign "$IDENTITY")
  if [[ -n "$KEYCHAIN_PATH" ]]; then
    PRODUCTBUILD_ARGS+=(--keychain "$KEYCHAIN_PATH")
  fi
else
  echo "[build-pkg] no signing identity given -- producing an UNSIGNED product archive." \
       "Only valid for local payload-layout testing, never for a shipped release." >&2
fi
PRODUCTBUILD_ARGS+=("$OUT_PKG")

productbuild "${PRODUCTBUILD_ARGS[@]}"

echo "[build-pkg] wrote $OUT_PKG"
