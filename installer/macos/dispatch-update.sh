#!/usr/bin/env bash
#
# Dispatch SMTP Relay - privileged update applier (macOS).
#
# Run (as root) by the app.dispatchrelay.dispatch-update LaunchDaemon when the web service drops an
# apply.request after verifying + staging an uploaded upgrade bundle. It re-verifies the bundle
# independently, swaps the release symlink, restarts the daemon, polls /health, and ROLLS BACK if the new
# version does not come up. The web UI watches status.json. This is installer/linux/dispatch-update.sh for
# launchd; read that file first, the sequence is the same.
#
# Where macOS differs, and why:
#   * The health gate has to be a /health poll. Linux gets it free from systemd (Type=notify makes
#     `systemctl restart` return only once the new version is ready) and Windows from the SCM. launchd's
#     `kickstart -k` returns as soon as it has signalled the old process, so "did it come back" has to be
#     asked of the app itself.
#   * No GNU-only tools: `sha256sum` -> `shasum -a 256`, `readlink -f` -> cd + pwd -P (BSD readlink has no
#     -f), and no python3 (on a bare Mac /usr/bin/python3 is a Command Line Tools stub that may not run), so
#     the manifest fields are read with tr/grep.
#   * Apple Silicon will not execute an unsigned Mach-O, so the apphost is ad-hoc re-signed after
#     extraction - the payload was signed when it was built, and extraction is allowed to move bytes.
#
# Trust: re-checks the detached signature against the shipped public key with openssl (defense-in-depth over
# the in-app C# check) and the payload sha256 against the manifest, both fail-closed.

set -uo pipefail

INSTALL_DIR="/opt/dispatch"
DATA_DIR="/var/db/dispatch"
UPDATES_DIR="$DATA_DIR/updates"
REQ="$UPDATES_DIR/apply.request"
PUBKEY="$INSTALL_DIR/dispatch-update-public.pem"
LABEL="app.dispatchrelay.dispatch"
KEEP_RELEASES=3
HEALTH_PORT="${DISPATCH_HEALTH_PORT:-8420}"
HEALTH_TIMEOUT=120

[ -f "$REQ" ] || exit 0

# launchd can start this job for more than one reason at a time (the watcher sees the directory change and
# the file change). Whoever loses the race must leave the swap to whoever won it.
LOCK="$UPDATES_DIR/.applier.lock"
mkdir "$LOCK" 2>/dev/null || { echo "update: another applier is running; nothing to do"; exit 0; }
trap 'rmdir "$LOCK" 2>/dev/null' EXIT

VER=""
status() {
  mkdir -p "$UPDATES_DIR"
  printf '{"state":"%s","version":"%s","message":"%s","updatedAtUtc":"%s"}\n' \
    "$1" "$VER" "$2" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$UPDATES_DIR/status.json"
}
# Extract a flat top-level JSON string field ($1) from a file ($2).
jget() { grep -o "\"$1\"[ ]*:[ ]*\"[^\"]*\"" "$2" | head -1 | sed 's/.*:[ ]*"//; s/"$//'; }

fail() { echo "update: $1" >&2; status "Failed" "$1"; rm -f "$REQ"; exit 1; }

# Absolute, symlink-free path of a directory. BSD readlink has no -f, so resolve it by cd'ing there:
# `cd` follows the links and `pwd -P` prints the physical path.
phys_dir() {
  local p=$1
  [ -d "$p" ] || return 1
  ( cd "$p" && pwd -P ) 2>/dev/null
}

# apply.request is written by the service account, so treat it as untrusted: take ONLY the staged-dir pointer
# from it (the authoritative version comes from the signed manifest below), and constrain that dir to
# strictly under <updates>/staged/ after resolving symlinks - so a compromised service can't aim the root
# applier at an arbitrary path (traversal / symlink escape).
STAGED="$(jget stagedDir "$REQ")"
[ -n "$STAGED" ] || fail "apply.request is missing stagedDir"
STAGED="$(phys_dir "$STAGED")" || STAGED=""
STAGED_BASE="$(phys_dir "$UPDATES_DIR/staged")" || STAGED_BASE=""
[ -n "$STAGED" ] && [ -n "$STAGED_BASE" ] || fail "could not resolve the staged dir"
case "$STAGED/" in
  "$STAGED_BASE"/*/) ;;
  *) fail "staged dir is outside the updates area: $STAGED" ;;
esac
[ -d "$STAGED" ] || fail "staged payload not found: $STAGED"
[ -f "$PUBKEY" ] || fail "release public key not found: $PUBKEY"

# This host's runtime id, which must match the manifest's artifact key AND the service's own idea of its
# arch (Dispatch.Web.UpdateService.CurrentArch): "osx-" + arm64|x64.
case "$(uname -m)" in
  arm64)           ARCH="osx-arm64" ;;
  x86_64)          ARCH="osx-x64" ;;
  *)               ARCH="osx-$(uname -m)" ;;
esac

# 1) Re-verify independently of the web service (fail-closed): the manifest signature (authenticity) FIRST,
#    then take the version + payload hash from the now-trusted manifest.
echo "update: verifying signature ($ARCH)"
openssl dgst -sha256 -verify "$PUBKEY" -signature "$STAGED/manifest.json.sig" "$STAGED/manifest.json" >/dev/null 2>&1 \
  || fail "signature verification failed"

# Authoritative version = the SIGNED manifest's version, and it must be a safe token (it lands in fs paths
# and an rm -rf target). Never trust apply.request's version for this.
VER="$(jget version "$STAGED/manifest.json")"
[ -n "$VER" ] || fail "signed manifest has no version"
case "$VER" in
  *[!A-Za-z0-9._+-]*) fail "refusing unsafe version string from manifest: $VER" ;;
esac

# Payload sha256 must match this arch's entry in the trusted manifest.
WANT="$(tr -d ' \n' < "$STAGED/manifest.json" | grep -o "\"$ARCH\":{[^}]*}" | grep -o '"sha256":"[^"]*"' | cut -d'"' -f4)"
GOT="$(shasum -a 256 "$STAGED/payload" | cut -d' ' -f1)"
[ -n "$WANT" ] && [ "$WANT" = "$GOT" ] || fail "payload checksum mismatch for $ARCH"

status "Applying" "applying $VER"

# 2) Best-effort DB backup before the restart (the new version auto-applies migrations on start). Never
#    blocks the update: a backup failure (no external DB, no pg_dump) is logged and skipped.
backup_db() {
  local pgdump
  pgdump="$(command -v pg_dump || true)"
  # Postgres.app keeps its binaries out of PATH; look there too before giving up.
  [ -n "$pgdump" ] || pgdump="$(ls -1 /Applications/Postgres.app/Contents/Versions/*/bin/pg_dump 2>/dev/null | tail -1)"
  [ -n "$pgdump" ] || { echo "update: pg_dump not found, skipping DB backup"; return 0; }
  # The connection string is one flat JSON string, so jget's regex is enough. A password containing a
  # double quote would need a real JSON parser - the Linux path has python3 for that, and macOS cannot
  # assume it. Worst case here is a skipped backup, never a corrupted one.
  local conn; conn="$(jget DispatchLog "$DATA_DIR/appsettings.json")"
  [ -n "$conn" ] || return 0
  local host port usr pwd db
  host="$(sed -n 's/.*[Hh]ost=\([^;]*\).*/\1/p' <<<"$conn")"
  port="$(sed -n 's/.*[Pp]ort=\([^;]*\).*/\1/p' <<<"$conn")"
  usr="$(sed -n 's/.*[Uu]sername=\([^;]*\).*/\1/p' <<<"$conn")"
  pwd="$(sed -n 's/.*[Pp]assword=\([^;]*\).*/\1/p' <<<"$conn")"
  db="$(sed -n 's/.*[Dd]atabase=\([^;]*\).*/\1/p' <<<"$conn")"
  [ -n "$port" ] || port="5432"
  [ -n "$host" ] && [ -n "$usr" ] && [ -n "$db" ] || { echo "update: could not parse connection, skipping DB backup"; return 0; }
  # db lands in a SQL identifier + a filename; require a safe token (VER was validated against the manifest).
  case "$db" in *[!A-Za-z0-9._-]*) echo "update: unsafe db name '$db', skipping DB backup"; return 0;; esac
  mkdir -p "$DATA_DIR/backups"
  local bak="$DATA_DIR/backups/${db}-pre-${VER}-$(date -u +%Y%m%dT%H%M%SZ).bak"
  if PGPASSWORD="$pwd" "$pgdump" -h "$host" -p "$port" -U "$usr" -Fc "$db" -f "$bak" >/dev/null 2>&1; then
    echo "update: DB backed up to $bak"
  else
    echo "update: DB backup failed (continuing) - rollback is binary-level; migrations are backward-compatible"
  fi
}
backup_db

# 3) Extract the new release and flip the 'current' symlink (remembering the old target for rollback).
NEWDIR="$INSTALL_DIR/releases/$VER"
PREV="$(phys_dir "$INSTALL_DIR/current")" || PREV=""
rm -rf "$NEWDIR"; mkdir -p "$NEWDIR"
# bsdtar has no --no-same-owner/--no-overwrite-dir; it also ignores archived ownership unless run with
# --preserve-permissions as root, so a plain extract is the right call here. Payload authenticity is already
# signature-bound, so this is the defense-in-depth layer, not the primary one.
tar -xzf "$STAGED/payload" -C "$NEWDIR" || fail "could not extract the payload"
chmod +x "$NEWDIR/Dispatch.Service" 2>/dev/null || true
# The apphost is the one binary this build pipeline modifies after it was signed (the payload was packed on
# a different machine), so give it a valid ad-hoc signature or dyld kills it on Apple Silicon. Leaving the
# runtime's own .dylib files untouched keeps Microsoft's signatures on them.
codesign --force --sign - "$NEWDIR/Dispatch.Service" >/dev/null 2>&1 \
  || echo "update: WARNING - could not ad-hoc sign the apphost; the daemon may be killed on start" >&2
chown -R root:wheel "$NEWDIR" 2>/dev/null || true
ln -sfn "$NEWDIR" "$INSTALL_DIR/current"

# 4) Restart, then ask the app whether it came back. This is the health gate: a process that launchd happily
#    respawns in a crash loop never passes it, and the old release is put back.
echo "update: restarting service on $VER"
status "Restarting" "restarting service on $VER (the dashboard will briefly disconnect)"
launchctl kickstart -k "system/${LABEL}" >/dev/null 2>&1

# Which ports to poll. The one baked into the watcher's plist at install time is only a starting guess: the
# dashboard port is webui.port, which lives in the database and can be changed in the UI. An applier that
# polled only the install-time value would roll back a perfectly good update on a host that moved the
# dashboard, so also try whatever the restarted service is actually listening on. (Ports that are not the
# dashboard fail the /health request rather than returning 200, so widening the set cannot produce a false
# pass.)
health_ports() {
  printf '%s\n' "$HEALTH_PORT"
  lsof -nP -iTCP -sTCP:LISTEN 2>/dev/null | awk '/Dispatch/ {print $9}' \
    | sed 's/.*://' | grep -E '^[0-9]+$' | sort -u
}

healthy() {
  local i p
  for i in $(seq 1 $((HEALTH_TIMEOUT / 5))); do
    for p in $(health_ports); do
      if [ "$(curl -sk -m 5 -o /dev/null -w '%{http_code}' "https://localhost:${p}/health" 2>/dev/null)" = "200" ]; then
        return 0
      fi
    done
    sleep 5
  done
  return 1
}

if healthy; then
  status "Succeeded" "updated to $VER"
  echo "update: now running $VER"
  # Prune old releases, keeping the newest few (never the current target).
  ls -1dt "$INSTALL_DIR"/releases/*/ 2>/dev/null | tail -n +$((KEEP_RELEASES + 1)) | while read -r d; do
    [ "$(phys_dir "$d")" = "$PREV" ] && continue
    [ "$(phys_dir "$d")" = "$(phys_dir "$INSTALL_DIR/current")" ] && continue
    rm -rf "$d"
  done
  rm -f "$REQ"
  exit 0
else
  echo "update: new version did not pass /health in ${HEALTH_TIMEOUT}s - rolling back" >&2
  if [ -n "$PREV" ] && [ -d "$PREV" ]; then
    ln -sfn "$PREV" "$INSTALL_DIR/current"
    launchctl kickstart -k "system/${LABEL}" >/dev/null 2>&1
    status "RolledBack" "update to $VER failed to start; rolled back to the previous version"
  else
    status "Failed" "update to $VER failed to start and no previous release was available to roll back to"
  fi
  rm -f "$REQ"
  exit 1
fi
