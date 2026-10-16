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
  # Checked on the staged tree, before anything is packed. The alternative is listing the finished package's
  # Payload, which means piping expand into cpio and hoping the flags match how pkgbuild compressed it -
  # that produces a warning nobody can act on. install.sh in particular is the whole reason the .pkg exists:
  # the postinstall runs that file, so a package without it is a wrapper around nothing.
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
# pkgutil exits non-zero when the package is unsigned, which is every package this script builds on CI, so
# the report is silenced. A status line that can abort the build it is reporting on is worse than no report.
# The `|| true` has to sit on the whole pipeline: with pipefail set, `head` finishing 0 does not save you
# from pkgutil's exit status, which is the one that fires.
pkgutil --check-signature "$OUT" 2>&1 | head -4 || true
