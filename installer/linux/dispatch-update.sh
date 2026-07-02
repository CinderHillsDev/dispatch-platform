#!/usr/bin/env bash
#
# Dispatch SMTP Relay - privileged update applier (Linux).
#
# Triggered (as root) by dispatch-update.path when the web service drops an apply.request after verifying +
# staging an uploaded upgrade bundle. This re-verifies the bundle independently, swaps the release symlink,
# restarts the service, and ROLLS BACK if the new version fails to come up. The web UI watches status.json.
#
# Trust: re-checks the detached signature against the shipped public key with openssl (defense-in-depth over
# the in-app C# check) and the payload sha256 against the manifest, both fail-closed.
#
set -uo pipefail

INSTALL_DIR="/opt/dispatch"
DATA_DIR="/var/lib/dispatch"
UPDATES_DIR="$DATA_DIR/updates"
REQ="$UPDATES_DIR/apply.request"
PUBKEY="$INSTALL_DIR/dispatch-update-public.pem"
KEEP_RELEASES=3

[ -f "$REQ" ] || exit 0

VER=""
status() {
  mkdir -p "$UPDATES_DIR"
  printf '{"state":"%s","version":"%s","message":"%s","updatedAtUtc":"%s"}\n' \
    "$1" "$VER" "$2" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$UPDATES_DIR/status.json"
}
# Extract a flat top-level JSON string field ($1) from a file ($2).
jget() { grep -o "\"$1\"[ ]*:[ ]*\"[^\"]*\"" "$2" | head -1 | sed 's/.*:[ ]*"//; s/"$//'; }

fail() { echo "update: $1" >&2; status "Failed" "$1"; rm -f "$REQ"; exit 1; }

# apply.request is written by the UNPRIVILEGED service, so treat it as untrusted: take ONLY the staged-dir
# pointer from it (the authoritative version comes from the signed manifest below), and constrain that dir to
# strictly under <updates>/staged/ after resolving symlinks - so a compromised service account can't aim the
# root applier at an arbitrary path (traversal / symlink escape).
STAGED="$(jget stagedDir "$REQ")"
[ -n "$STAGED" ] || fail "apply.request is missing stagedDir"
STAGED="$(readlink -f "$STAGED" 2>/dev/null || true)"
STAGED_BASE="$(readlink -f "$UPDATES_DIR/staged" 2>/dev/null || true)"
[ -n "$STAGED" ] && [ -n "$STAGED_BASE" ] || fail "could not resolve the staged dir"
case "$STAGED/" in
  "$STAGED_BASE"/*/) ;;
  *) fail "staged dir is outside the updates area: $STAGED" ;;
esac
[ -d "$STAGED" ] || fail "staged payload not found: $STAGED"
[ -f "$PUBKEY" ] || fail "release public key not found: $PUBKEY"

# This host's runtime id, to pick its entry out of the package manifest's artifacts map.
case "$(uname -m)" in
  x86_64|amd64)  ARCH="linux-x64" ;;
  aarch64|arm64) ARCH="linux-arm64" ;;
  *)             ARCH="linux-$(uname -m)" ;;
esac

# 1) Re-verify independently of the web service (fail-closed): the manifest signature (authenticity) FIRST,
#    then take the version + payload hash from the now-trusted manifest.
echo "update: verifying signature ($ARCH)"
openssl dgst -sha256 -verify "$PUBKEY" -signature "$STAGED/manifest.json.sig" "$STAGED/manifest.json" >/dev/null 2>&1 \
  || fail "signature verification failed"

# Authoritative version = the SIGNED manifest's version, and it must be a safe token (it lands in fs paths,
# an rm -rf target, and a T-SQL backup filename). Never trust apply.request's version for this.
VER="$(jget version "$STAGED/manifest.json")"
[ -n "$VER" ] || fail "signed manifest has no version"
case "$VER" in
  *[!A-Za-z0-9._+-]*) fail "refusing unsafe version string from manifest: $VER" ;;
esac

# Payload sha256 must match this arch's entry in the trusted manifest. Read the manifest with python via
# ARGV + env (never string-interpolated into the program text) so a crafted path/arch can't inject code.
if command -v python3 >/dev/null 2>&1; then
  WANT="$(DISPATCH_ARCH="$ARCH" python3 - "$STAGED/manifest.json" 2>/dev/null <<'PY'
import json, os, sys
try:
    m = json.load(open(sys.argv[1]))
    print(m["artifacts"][os.environ["DISPATCH_ARCH"]]["sha256"])
except Exception:
    pass
PY
)"
  GOT="$(sha256sum "$STAGED/payload" | cut -d' ' -f1)"
  [ -n "$WANT" ] && [ "$WANT" = "$GOT" ] || fail "payload checksum mismatch for $ARCH"
else
  echo "update: python3 absent - skipping the payload-hash recheck (signature already verified authenticity)"
fi

status "Applying" "applying $VER"

# 2) Best-effort DB backup before the restart (the new version auto-applies migrations on start). Never
#    blocks the update: a backup failure (e.g. external SQL, no sqlcmd) is logged and skipped.
backup_db() {
  command -v sqlcmd >/dev/null 2>&1 || { echo "update: sqlcmd not found, skipping DB backup"; return 0; }
  command -v python3 >/dev/null 2>&1 || return 0
  # Read the connection string via python ARGV (not interpolated into the program text).
  local conn; conn="$(python3 - "$DATA_DIR/appsettings.json" 2>/dev/null <<'PY'
import json, sys
try:
    print(json.load(open(sys.argv[1]))["ConnectionStrings"]["DispatchLog"])
except Exception:
    pass
PY
)"
  [ -n "$conn" ] || return 0
  local srv usr pwd db;
  srv="$(sed -n 's/.*[Ss]erver=\([^;]*\).*/\1/p' <<<"$conn")"
  usr="$(sed -n 's/.*[Uu]ser[ ]*[Ii]d=\([^;]*\).*/\1/p' <<<"$conn")"
  pwd="$(sed -n 's/.*[Pp]assword=\([^;]*\).*/\1/p' <<<"$conn")"
  db="$(sed -n 's/.*[Dd]atabase=\([^;]*\).*/\1/p' <<<"$conn")"
  [ -n "$srv" ] && [ -n "$usr" ] && [ -n "$db" ] || { echo "update: could not parse connection, skipping DB backup"; return 0; }
  # db lands in a T-SQL identifier + a filename; require a safe token (VER was validated against the manifest).
  case "$db" in *[!A-Za-z0-9._-]*) echo "update: unsafe db name '$db', skipping DB backup"; return 0;; esac
  mkdir -p "$DATA_DIR/backups"
  local bak="$DATA_DIR/backups/${db}-pre-${VER}-$(date -u +%Y%m%dT%H%M%SZ).bak"
  if sqlcmd -S "$srv" -U "$usr" -P "$pwd" -C -Q "BACKUP DATABASE [$db] TO DISK = N'$bak' WITH INIT, COMPRESSION;" >/dev/null 2>&1; then
    echo "update: DB backed up to $bak"
  else
    echo "update: DB backup failed (continuing) - rollback is binary-level; migrations are backward-compatible"
  fi
}
backup_db

# 3) Extract the new release and flip the 'current' symlink (remembering the old target for rollback).
NEWDIR="$INSTALL_DIR/releases/$VER"
PREV="$(readlink -f "$INSTALL_DIR/current" 2>/dev/null || true)"
rm -rf "$NEWDIR"; mkdir -p "$NEWDIR"
# Hardened extraction: don't honour archived ownership or clobber dir metadata. Payload authenticity is
# already signature-bound, so this is defense-in-depth against a malicious release archive.
tar --no-same-owner --no-overwrite-dir -xzf "$STAGED/payload" -C "$NEWDIR" || fail "could not extract the payload"
chmod +x "$NEWDIR/Dispatch.Service" 2>/dev/null || true
chown -R dispatch:dispatch "$NEWDIR" 2>/dev/null || true
ln -sfn "$NEWDIR" "$INSTALL_DIR/current"

# 4) Restart. The unit is Type=notify, so `systemctl restart` only returns success once the new version has
#    fully started (DB migrations applied, listeners bound, READY signalled) - that IS the health gate.
echo "update: restarting service on $VER"
status "Restarting" "restarting service on $VER (the dashboard will briefly disconnect)"
if systemctl restart dispatch; then
  status "Succeeded" "updated to $VER"
  echo "update: now running $VER"
  # Prune old releases, keeping the newest few (never the current target).
  ls -1dt "$INSTALL_DIR"/releases/*/ 2>/dev/null | tail -n +$((KEEP_RELEASES + 1)) | while read -r d; do
    [ "$(readlink -f "$d")" = "$PREV" ] && continue
    [ "$(readlink -f "$d")" = "$(readlink -f "$INSTALL_DIR/current")" ] && continue
    rm -rf "$d"
  done
  rm -f "$REQ"
  exit 0
else
  echo "update: new version failed to start - rolling back" >&2
  if [ -n "$PREV" ] && [ -d "$PREV" ]; then
    ln -sfn "$PREV" "$INSTALL_DIR/current"
    systemctl restart dispatch || true
    status "RolledBack" "update to $VER failed to start; rolled back to the previous version"
  else
    status "Failed" "update to $VER failed to start and no previous release was available to roll back to"
  fi
  rm -f "$REQ"
  exit 1
fi
