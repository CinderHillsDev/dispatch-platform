#!/usr/bin/env bash
#
# build-upgrade-package.sh - build a SIGNED cross-platform upgrade package for testing web-UI self-update,
# WITHOUT cutting a release tag. Mirrors the "Build + sign the upgrade package" step in release.yml exactly:
# self-contained payload(s) + one signed manifest, all in dispatch-upgrade-<ver>.tar.gz.
#
# The package is signed with YOUR private key (the same one in the DISPATCH_UPDATE_SIGNING_KEY GitHub secret,
# matching the committed src/Dispatch.Core/Updates/dispatch-update-public.pem). An unsigned/tampered package
# is refused by the updater, so the key is required.
#
# Usage:
#   scripts/build-upgrade-package.sh -k ~/dispatch-update-private.pem -v 0.2.1            # all 3 arches
#   scripts/build-upgrade-package.sh -k ~/dispatch-update-private.pem -v 0.2.1 -q         # quick: linux-x64 only
#   scripts/build-upgrade-package.sh -k ~/dispatch-update-private.pem -v 0.2.1 -o /tmp    # output dir
#
set -euo pipefail

KEYFILE=""; VER=""; OUTDIR="."; QUICK=0
while [ $# -gt 0 ]; do
  case "$1" in
    -k|--key)     KEYFILE="$2"; shift 2;;
    -v|--version) VER="$2"; shift 2;;
    -o|--out)     OUTDIR="$2"; shift 2;;
    -q|--quick)   QUICK=1; shift;;
    -h|--help)    sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//'; exit 0;;
    *) echo "unknown option: $1" >&2; exit 1;;
  esac
done

[ -n "$KEYFILE" ] || { echo "ERROR: -k <private-key.pem> is required." >&2; exit 1; }
[ -f "$KEYFILE" ] || { echo "ERROR: key file not found: $KEYFILE" >&2; exit 1; }
[ -n "$VER" ]     || { echo "ERROR: -v <version> is required (e.g. 0.2.1)." >&2; exit 1; }

# Resolve repo root from this script's location so it runs from anywhere.
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PUBKEY="$ROOT/src/Dispatch.Core/Updates/dispatch-update-public.pem"
SVC="$ROOT/src/Dispatch.Service"
[ -f "$PUBKEY" ] || { echo "ERROR: public key not found at $PUBKEY" >&2; exit 1; }

# Portable sha256 (macOS has no sha256sum) via openssl, which we already need for signing.
sha256() { openssl dgst -sha256 "$1" | awk '{print $NF}'; }

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
PKG="$WORK/pkg"; mkdir -p "$PKG"

# Which runtimes to build. Quick = just this-host-class Linux x64 (enough to test the Linux appliance).
if [ "$QUICK" = 1 ]; then RIDS="linux-x64"; else RIDS="linux-x64 linux-arm64 win-x64"; fi
echo "==> Building payloads for: $RIDS (version $VER)"

artifacts=""
for rid in $RIDS; do
  out="$WORK/publish/$rid"
  dotnet publish "$SVC" -c Release -r "$rid" --self-contained true -p:Version="$VER" -o "$out" >/dev/null
  case "$rid" in
    win-x64) file="win-x64.zip";        ( cd "$out" && zip -qr - . ) > "$PKG/$file" ;;
    *)       file="$rid.tar.gz";         tar -czf "$PKG/$file" -C "$out" . ;;
  esac
  sh="$(sha256 "$PKG/$file")"
  [ -n "$artifacts" ] && artifacts="$artifacts,"
  artifacts="$artifacts\"$rid\":{\"file\":\"$file\",\"sha256\":\"$sh\"}"
  echo "    $rid -> $file  ($sh)"
done

built="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
printf '{"name":"dispatch","version":"%s","minFromVersion":"0.0.0","builtAt":"%s","notesUrl":"%s","artifacts":{%s}}\n' \
  "$VER" "$built" "https://github.com/chrismuench/Dispatch-SMTP-Relay/releases" "$artifacts" > "$PKG/manifest.json"

echo "==> Signing manifest"
openssl dgst -sha256 -sign "$KEYFILE" -out "$PKG/manifest.json.sig" "$PKG/manifest.json"
# Self-check: must verify against the committed public key, else the appliance would refuse it.
if openssl dgst -sha256 -verify "$PUBKEY" -signature "$PKG/manifest.json.sig" "$PKG/manifest.json" >/dev/null; then
  echo "    signature verifies against $PUBKEY"
else
  echo "ERROR: signature does NOT verify against the committed public key - wrong private key?" >&2
  exit 1
fi

mkdir -p "$OUTDIR"
OUT="$(cd "$OUTDIR" && pwd)/dispatch-upgrade-${VER}.tar.gz"
tar -czf "$OUT" -C "$PKG" .
echo "==> Done: $OUT"
ls -lh "$OUT"
