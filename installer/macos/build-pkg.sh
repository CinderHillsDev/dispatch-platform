#!/bin/bash
# Build the macOS .pkg installer from a staged release directory.
#
#   ./build-pkg.sh <staged-dir> <version> <out.pkg>
#
# <staged-dir> is exactly what the universal tarball carries: install.sh, dispatch-update.sh, the two
# LaunchDaemon property lists, dispatch-update-public.pem, and bin-arm64/ + bin-x64/. Both CI and
# release.yml call THIS script, so the artifact a pull request installs and the artifact a customer
# downloads are produced by one code path. A .pkg nobody installs until a tag is cut is a .pkg that ships
# broken, and release.yml only runs on a tag.
#
# The payload is staged at /opt/dispatch/pkg rather than at its final home on purpose. Nothing in here
# decides where Dispatch lives, how the plists are substituted, or which CPU to install for - the payload's
# own postinstall execs the install.sh that landed next to it, which already decides all of that. That
# reuse is the entire reason a .pkg was worth building: no second copy of the install logic to drift.
#
# Signing: set DISPATCH_PKG_SIGN_IDENTITY ("Developer ID Installer: <org>") to have pkgbuild sign the
# product. The hosted macOS runners report 0 valid identities, so a .pkg from CI is unsigned and Gatekeeper
# refuses it as a double-clickable download - see docs/macos.md for what the operator types instead, and
# what wiring a certificate plus notarytool would change.
#
# macOS ships Bash 3.2, so no Bash-4 constructs (and no empty-array "${a[@]}" expansion under set -u).

set -euo pipefail

[[ $# -eq 3 ]] || { echo "usage: $0 <staged-dir> <version> <out.pkg>" >&2; exit 2; }
STAGED="$1" VER="$2" OUT="$3"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

[[ -d "$STAGED" ]] || { echo "no such staged directory: $STAGED" >&2; exit 1; }
[[ -f "$SCRIPT_DIR/pkg/postinstall" ]] || { echo "missing $SCRIPT_DIR/pkg/postinstall" >&2; exit 1; }
for f in install.sh dispatch-update.sh app.dispatchrelay.dispatch.plist \
         app.dispatchrelay.dispatch-update.plist dispatch-update-public.pem; do
  [[ -f "$STAGED/$f" ]] || { echo "staged dir is missing $f" >&2; exit 1; }
done
# At least one arch must be present. A real release carries both and install.sh picks by uname -m at install
# time; CI can only publish the arch the runner executes, so one is legitimately absent there.
[[ -d "$STAGED/bin-arm64" || -d "$STAGED/bin-x64" ]] \
  || { echo "staged dir has neither bin-arm64/ nor bin-x64/ - a .pkg with no binaries is a wrapper around nothing" >&2; exit 1; }
for d in "$STAGED"/bin-*; do
  [[ -d "$d" ]] || continue
  [[ -f "$d/Dispatch.Service" ]] || { echo "$d has no Dispatch.Service" >&2; exit 1; }
done

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
PAYLOAD="$WORK/payload/opt/dispatch/pkg"
mkdir -p "$PAYLOAD" "$WORK/scripts"
cp -R "$STAGED/." "$PAYLOAD/"

# pkgbuild records the modes it is handed, and the receipt/Archive payload is what installer(8) restores, so
# an unexecutable install.sh here becomes a .pkg that fails at install time on the customer's machine. The
# macOS installer scripts shipped mode 644 in git for exactly this reason; normalise regardless of source.
chmod 755 "$PAYLOAD/install.sh" "$PAYLOAD/dispatch-update.sh"
for d in "$PAYLOAD"/bin-*; do
  # if/fi, not `[[ -d "$d" ]] && chmod`: a short-circuited AND-list is the last command of the loop body, so
  # under set -e it aborts the build when a glob matched nothing (a single-arch CI staging, say).
  if [[ -d "$d" ]]; then chmod 755 "$d/Dispatch.Service"; fi
done
cp "$SCRIPT_DIR/pkg/postinstall" "$WORK/scripts/postinstall"
chmod 755 "$WORK/scripts/postinstall"

# pkgbuild wants a dotted numeric version for the receipt (that is what it compares for upgrade detection).
# A prerelease like 0.7.3-rc.1, or CI's 0.0.0-ci, is trimmed to its numeric prefix; the full version still
# names the file and the release directory install.sh creates.
PKG_VER="$(printf '%s' "$VER" | sed -E 's/^v//; s/^([0-9]+(\.[0-9]+)*).*/\1/')"
if ! printf '%s' "$PKG_VER" | grep -Eq '^[0-9]+(\.[0-9]+)*$'; then
  echo "warning: version '$VER' has no dotted-numeric prefix; the receipt will claim 0.0.0" >&2
  PKG_VER="0.0.0"
fi
[[ "$PKG_VER" == "$VER" ]] || echo "note: receipt version '$VER' -> '$PKG_VER' (the full string still names the artifact)"

BUILDFLAGS=(--root "$WORK/payload" --scripts "$WORK/scripts"
            --identifier app.dispatchrelay.dispatch --version "$PKG_VER" --install-location /)
if [[ -n "${DISPATCH_PKG_SIGN_IDENTITY:-}" ]]; then
  echo "signing with identity: $DISPATCH_PKG_SIGN_IDENTITY"
  BUILDFLAGS+=(--sign "$DISPATCH_PKG_SIGN_IDENTITY")
fi

pkgbuild "${BUILDFLAGS[@]}" "$OUT"

ls -l "$OUT"
echo "=== signature posture of the artifact just built ==="
# '|| true' is load bearing: pkgutil exits non-zero when a pkg is unsigned, and every .pkg CI builds is
# unsigned (the hosted runners have no Developer ID identity). Without it a release dies on its own status
# report - and dies only on the real builds, never on a machine where someone signed one by hand.
pkgutil --check-signature "$OUT" 2>&1 | head -4 || true

# Belt and braces: prove the payload really carries install.sh. expand + cpio are present on macOS, but a
# difference in either is not worth failing a release over - the CI smoke installs the pkg for real, and that
# is the check that matters. Count the matches into a variable rather than piping grep -q: with pipefail set,
# grep -q's early exit would SIGPIPE cpio and report a good payload as a missing one.
if command -v expand >/dev/null 2>&1 && command -v cpio >/dev/null 2>&1; then
  hits="$( { expand -p Payload "$OUT" 2>/dev/null; } | { cpio -t 2>/dev/null; } | grep -c 'install\.sh$' || true )"
  if [[ "${hits:-0}" != "0" ]]; then
    echo "OK: payload carries install.sh (the .pkg installs through it, not through a copy of it)"
  else
    echo "warning: could not list install.sh inside the payload - check the pkgbuild --root layout" >&2
  fi
fi
