#!/bin/bash
# Build DispatchRelay.app - the macOS menu bar control panel - and wrap it in a drag-to-Applications .dmg.
#
#   ./build-app.sh <staged-dir> <version> <output-dmg>
#
# <staged-dir> is what the release job assembles for the universal tarball: install.sh, dispatch-update.sh,
# the two LaunchDaemon property lists, dispatch-update-public.pem, and bin-arm64/ (+ bin-x64/, which this
# script deliberately drops). The payload is copied INTO the bundle, so DispatchRelay.app is a complete installer:
# nobody has to find a second download to install the service after moving the app.
#
# Apple Silicon only. That is a product decision, not an oversight: an arm64-only bundle halves the download,
# removes the "which bin-<arch> does this Mac want" question from the install path entirely, and matches the
# machines Dispatch is actually being run on here. An Intel Mac still has the universal tarball and the .pkg.
#
# Signing is ad-hoc. A Developer ID + notarization would make the first open frictionless; without it,
# Gatekeeper blocks a quarantined download once and right-click > Open gets around it (or run the app once
# with `xattr -d com.apple.quarantine`). Everything else here works unsigned.

set -euo pipefail

STAGED="${1:?usage: build-app.sh <staged-dir> <version> <output-dmg>}"
VER="${2:?missing version}"
OUT="${3:?missing output path}"

command -v swiftc   >/dev/null 2>&1 || { echo "swiftc not found (needs Xcode or the Command Line Tools)" >&2; exit 1; }
command -v hdiutil  >/dev/null 2>&1 || { echo "hdiutil not found - this is not macOS" >&2; exit 1; }
command -v codesign >/dev/null 2>&1 || { echo "codesign not found" >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd -P)"
APP_NAME="DispatchRelay.app"
WORK="$(mktemp -d /tmp/dispatch-app.XXXXXX)"
trap 'rm -rf "$WORK"' EXIT

STAGED="$(cd "$STAGED" && pwd -P)"
[[ -f "$STAGED/install.sh" ]]            || { echo "staged dir has no install.sh: $STAGED" >&2; exit 1; }
[[ -x "$STAGED/install.sh" ]]            || { echo "staged install.sh is not executable (pkgbuild/cp keeps modes, so an unexecutable source ships an unrunnable installer)" >&2; exit 1; }
[[ -d "$STAGED/bin-arm64" ]]             || { echo "staged dir has no bin-arm64/ - this bundle is Apple Silicon only and would ship an app that installs nothing" >&2; exit 1; }
[[ -f "$STAGED/bin-arm64/Dispatch.Service" ]] || { echo "bin-arm64 has no Dispatch.Service" >&2; exit 1; }

APP="$WORK/$APP_NAME"
BIN_DIR="$APP/Contents/MacOS"
RES_DIR="$APP/Contents/Resources"
mkdir -p "$BIN_DIR" "$RES_DIR"

# ---- payload: the same files the tarball ships, minus the other architecture -----------------------------
mkdir -p "$RES_DIR/payload"
for f in install.sh dispatch-update.sh dispatch-update-public.pem \
         app.dispatchrelay.dispatch.plist app.dispatchrelay.dispatch-update.plist README.txt; do
  if [[ -f "$STAGED/$f" ]]; then /bin/cp "$STAGED/$f" "$RES_DIR/payload/"; fi
done
[[ -f "$RES_DIR/payload/README.txt" ]] || echo "note: no README.txt in the staged dir (the tarball's copy is generated in release.yml)" >&2
/bin/cp -R "$STAGED/bin-arm64" "$RES_DIR/payload/bin-arm64"
chmod 755 "$RES_DIR/payload" "$RES_DIR/payload/install.sh" "$RES_DIR/payload/dispatch-update.sh" 2>/dev/null || true

# ---- executable ------------------------------------------------------------------------------
# One source file, no package manager, no Xcode project. The deployment target is the bundle's
# LSMinimumSystemVersion so a newer SDK on the runner cannot quietly produce a binary that will not
# launch on an older Mac.
# -module-name is not cosmetic. Swift derives the module name from the OUTPUT FILE name, and "Dispatch" is
# already the name of Apple's libdispatch module - which Foundation imports. Naming the module Dispatch makes
# the compiler report "circular dependency between modules 'Dispatch' and 'Foundation'", which is a confusing
# way to say that an app cannot be its own namesake. The executable keeps the name the bundle expects.
xcrun swiftc -O -target arm64-apple-macos13.0 \
  -module-name dispatchrelay \
  -framework AppKit \
  -o "$BIN_DIR/dispatchrelay" \
  "$SCRIPT_DIR/app/Sources/main.swift"
chmod 755 "$BIN_DIR/dispatchrelay"

# ---- Info.plist, version-stamped -------------------------------------------------------------
/bin/cp "$SCRIPT_DIR/app/Info.plist" "$APP/Contents/Info.plist"
# Set only rewrites keys that exist, and Info.plist declares no CFBundleIconFile on purpose (the icon is
# conditional, see below), so the icon key is added here rather than set. Failing to notice that is how the
# build dies with "Entry, :CFBundleIconFile, Does Not Exist" on a machine that has nothing wrong with it.
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VER" \
                        -c "Set :CFBundleVersion $VER" "$APP/Contents/Info.plist" >/dev/null
printf 'APPL????' > "$APP/Contents/PkgInfo"

# An icon is what makes an app findable in /Applications, and a menu bar app with a generic icon reads as a
# half-finished tool. If the source PNG is present, build an .icns with the stock tools and add the
# CFBundleIconFile key; if not, ship with no key at all rather than a reference to a file that is not there.
ICON_SRC="$SCRIPT_DIR/app/Resources/DispatchIcon.png"
if [[ -f "$ICON_SRC" ]] && command -v iconutil >/dev/null 2>&1; then
  ICONSET="$WORK/AppIcon.iconset"
  mkdir -p "$ICONSET"
  for size in 16 32 64 128 256 512; do
    sips -z "$size" "$size" "$ICON_SRC" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null 2>&1 || true
    double=$((size * 2))
    sips -z "$double" "$double" "$ICON_SRC" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null 2>&1 || true
  done
  if iconutil -c icns "$ICONSET" -o "$RES_DIR/AppIcon.icns" >/dev/null 2>&1; then
    # Separate invocations: PlistBuddy stops at the first failing -c, and Delete on a key that was never
    # declared (Info.plist declares no icon, since this branch is conditional) counts as a failure.
    /usr/libexec/PlistBuddy -c "Delete :CFBundleIconFile" "$APP/Contents/Info.plist" >/dev/null 2>&1 || true
    /usr/libexec/PlistBuddy -c "Add :CFBundleIconFile string AppIcon" "$APP/Contents/Info.plist" >/dev/null
    echo "icon: built AppIcon.icns"
  else
    echo "icon: iconutil failed; shipping with the generic icon" >&2
  fi
else
  echo "icon: no $ICON_SRC; shipping without one" >&2
  /usr/libexec/PlistBuddy -c "Delete :CFBundleIconFile" "$APP/Contents/Info.plist" >/dev/null 2>&1 || true
fi

# ---- sign: inner binary first, then the bundle ------------------------------------------------
# Everything that writes Info.plist or adds resources has to happen above this line: codesign seals the
# bundle's contents, so touching it afterwards makes --verify fail with a "resource envelope is obsolete"
# error that looks like a signing problem and is really an ordering one.
# Ad-hoc, no hardened runtime. Hardened runtime with no entitlements buys nothing here and can block the
# .NET runtime the payload carries, and the daemon is launched by launchd from /opt rather than by this
# bundle, so the app's own options do not constrain it.
# DISPATCH_APP_SIGN_IDENTITY turns the ad-hoc seal into a Developer ID one when a runner ever has a
# certificate; the notary step still has to be added on top (see docs/macos.md), so this alone does not make
# the download frictionless. Default stays ad-hoc, which is honest about what CI can do today.
IDENTITY="${DISPATCH_APP_SIGN_IDENTITY:--}"
[[ "$IDENTITY" == "-" ]] && echo "signing: ad-hoc (set DISPATCH_APP_SIGN_IDENTITY to sign with a Developer ID)" || echo "signing: $IDENTITY"
# Not an array: /bin/bash on macOS is 3.2, and expanding an empty array under `set -u` is an unbound
# variable error there, which would fail every ad-hoc-signed build. A plain string that word-splits away to
# nothing is the portable form.
OPTS=""
[[ "$IDENTITY" != "-" ]] && OPTS="--options runtime"
codesign --force --sign "$IDENTITY" --timestamp=none $OPTS "$BIN_DIR/dispatchrelay" >/dev/null
codesign --force --sign "$IDENTITY" --timestamp=none $OPTS "$APP" >/dev/null
codesign --verify --verbose=2 "$APP" 2>&1 | tail -3

# ---- dmg: the app plus an Applications shortcut, so the install really is a drag --------------
DMG_ROOT="$WORK/image"
mkdir -p "$DMG_ROOT"
mv "$APP" "$DMG_ROOT/"
ln -s /Applications "$DMG_ROOT/Applications"
if [[ -f "$RES_DIR/payload/README.txt" ]]; then cp "$RES_DIR/payload/README.txt" "$WORK/README.txt"; fi

hdiutil create -volname "Dispatch Relay $VER" -srcfolder "$DMG_ROOT" -ov -format UDZO "$OUT" >/dev/null
chmod 644 "$OUT"

echo "built: $OUT"
echo "  app:      $APP_NAME ($(du -sh "$DMG_ROOT/$APP_NAME" | cut -f1 | tr -d ' ') on disk, arm64 only)"
echo "  payload:  install.sh + bin-arm64 ($(find "$DMG_ROOT/$APP_NAME/Contents/Resources/payload" -type f | wc -l | tr -d ' ') files)"
echo "  signed:   ad-hoc (notarization needs a Developer ID this project does not have)"
