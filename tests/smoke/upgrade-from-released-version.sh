#!/usr/bin/env bash
#
# The real binary upgrade: install a PUBLISHED release, run it, then upgrade to this build in place.
#
# Everything else in tests/smoke exercises this build against a database shaped like an old one. This
# installs the actual shipped artifact from GitHub Releases - old installer, old binaries, old schema
# written by old code - and then upgrades it the way an operator does: extract the new tarball and re-run
# install.sh over the top.
#
# It exists because two upgrade defects were invisible without it:
#
#   * the migrator refused a pre-0.7 database outright, while the migration test passed because it built
#     its source with current code;
#   * install.sh rewrites appsettings.json wholesale, so once SQLite became the default an in-place upgrade
#     with no flags would silently repoint a PostgreSQL install at a new empty database - a service that
#     starts, looks healthy, and shows none of the customer's mail.
#
# Linux only: install.sh drives systemd. The released tarball is universal (bin-x64 / bin-arm64) and
# its installer selects by architecture, so no --prebuilt is passed to either side.
#
# Usage:  tests/smoke/upgrade-from-released-version.sh <path-to-new-tarball.tar.gz> [release-tag]
#
set -euo pipefail

readonly NEW_TARBALL="${1:?usage: upgrade-from-released-version.sh <new-tarball.tar.gz> [release-tag]}"
readonly RELEASE_TAG="${2:-}"
readonly WORK="$(mktemp -d)"
readonly ADMIN_PW='Upgrade_Rel_Adm1n!'
readonly DB_PW='Upgrade_Rel_Db1!'
readonly DASH="https://localhost:8420"
readonly API="http://localhost:8025"
readonly JAR="$WORK/cookies"

cleanup() { sudo systemctl stop dispatch 2>/dev/null || true; rm -rf "$WORK"; }
trap cleanup EXIT

say()  { printf '\n\033[1m== %s\033[0m\n' "$*"; }
ok()   { printf '   \033[32mok\033[0m   %s\n' "$*"; }
fail() {
    printf '   \033[31mFAIL\033[0m %s\n' "$*" >&2
    # Everything to stderr. These helpers get called inside $( ) command substitutions, which capture
    # stdout - so diagnostics written there vanish into the variable being assigned instead of the log.
    printf '\n   --- last 25 journal lines ---\n' >&2
    sudo journalctl -u dispatch --no-pager -n 25 >&2 2>/dev/null || true
    printf '   --- database file ---\n' >&2
    sudo ls -l /var/lib/dispatch/dispatch.db* >&2 2>/dev/null || true
    exit 1
}

dget()  { curl -sk -b "$JAR" -c "$JAR" "$DASH$1"; }
dpost() { curl -sk -b "$JAR" -c "$JAR" -H 'Content-Type: application/json' -H 'X-Dispatch-Request: 1' -X POST -d "$2" "$DASH$1"; }
jq_()   { python3 -c "
import sys, json
raw = sys.stdin.read()
try: d = json.loads(raw)
except Exception:
    sys.stderr.write('   raw: %s\n' % (raw[:300] if raw.strip() else '(empty)')); sys.exit(3)
print($1)"; }

wait_healthy() {
    for _ in $(seq 1 60); do
        curl -fsSk -m 5 "$DASH/health" >/dev/null 2>&1 && return 0
        sleep 5
    done
    return 1
}

# Creates an API key and returns its plaintext. Reports the response body when there is no key in it:
# /api/keys answers 200 with the key, or 400 with {"error": ...}, and both are valid JSON - so extracting
# the field blind turns a real API error into an opaque KeyError with nothing to diagnose from.
create_key() {
    local name="$1" body
    body="$(dpost /api/keys "{\"name\":\"$name\",\"rateLimitPerMinute\":0}")"
    local key; key="$(printf '%s' "$body" | jq_ "d.get('key', '')" 2>/dev/null || true)"
    [[ -n "$key" ]] || fail "creating API key '$name' returned no key. Response: ${body:-(empty)}"
    printf '%s' "$key"
}

authenticate() {
    rm -f "$JAR"
    local needs; needs=$(dget /api/auth/status | jq_ "str(d.get('needsSetup', False)).lower()")
    if [[ "$needs" == "true" ]]; then
        dpost /api/auth/password "{\"password\":\"$ADMIN_PW\"}" >/dev/null
    else
        dpost /api/auth/login "{\"password\":\"$ADMIN_PW\"}" >/dev/null
    fi
}

# ==================================================================================================
say "0. Start from a clean machine"
# Earlier steps in the same CI job install Dispatch. Inheriting one would make this test the wrong thing
# entirely: the released installer would land on an existing config, and "upgrade" would no longer mean
# upgrading from the released version.
sudo systemctl stop dispatch 2>/dev/null || true
sudo systemctl disable dispatch 2>/dev/null || true
sudo rm -rf /opt/dispatch /var/lib/dispatch /var/log/dispatch
sudo -u postgres psql -c 'DROP DATABASE IF EXISTS "DispatchLog";' >/dev/null 2>&1 || true
ok "removed any existing install"

say "1. Download the published release"
tag="$RELEASE_TAG"
[[ -z "$tag" ]] && tag="$(gh release list --limit 20 --json tagName,isDraft \
    --jq '[.[] | select(.isDraft == false)] | .[0].tagName')"
[[ -n "$tag" ]] || fail "could not determine a published release tag"

asset="$(gh release view "$tag" --json assets --jq '.assets[].name | select(test("linux.*\\.tar\\.gz$"))' | head -n1)"
[[ -n "$asset" ]] || fail "release $tag has no linux tarball asset"
gh release download "$tag" --pattern "$asset" --dir "$WORK" || fail "could not download $asset from $tag"
ok "downloaded $asset from $tag"

mkdir -p "$WORK/old" && tar -xzf "$WORK/$asset" -C "$WORK/old"
old_dir="$(find "$WORK/old" -maxdepth 1 -mindepth 1 -type d | head -n1)"
[[ -x "$old_dir/install.sh" ]] || fail "released tarball has no install.sh"
ok "extracted to $(basename "$old_dir")"

say "2. Install the released version, on PostgreSQL, exactly as its own docs say"
# The old installer provisions PostgreSQL itself - the behaviour this branch removes. That is the install
# the one existing customer is running, so it is what the upgrade has to start from.
# No --prebuilt: the released tarball is universal (bin-x64 / bin-arm64) and its installer picks the right
# one by architecture. This is verbatim the command its own README gives, which is the point - the test is
# worth less the moment it stops being what an operator would actually type.
sudo "$old_dir/install.sh" --install-postgres \
    --db-password "$DB_PW" --admin-password "$ADMIN_PW" >"$WORK/install-old.log" 2>&1 \
    || { tail -30 "$WORK/install-old.log"; fail "the released installer failed"; }

wait_healthy || fail "the released version did not come up"
ok "released version is running"

OLD_CS="$(sudo sed -n 's/.*"DispatchLog"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' /var/lib/dispatch/appsettings.json | head -n1)"
[[ "$OLD_CS" == Host=* ]] || fail "expected a PostgreSQL connection string, got: $OLD_CS"
ok "installed against PostgreSQL"

say "3. Put real mail through the OLD version"
authenticate
RELAY_ID=$(dpost /api/relays '{"name":"released-relay","provider":"Local"}' | jq_ "d['id']")
dpost "/api/relays/$RELAY_ID/set-default" '{}' >/dev/null
API_KEY="$(create_key released-key)"
for i in $(seq 1 20); do
    curl -s -X POST "$API/api/v1/messages" -H "Authorization: Bearer $API_KEY" \
        -H 'Content-Type: application/json' \
        -d "{\"from\":\"old@local.test\",\"to\":[\"dest@local.test\"],\"subject\":\"sent on the old version $i\",\"text\":\"body\"}" >/dev/null
done
sleep 6
OLD_ROWS=$(sudo -u postgres psql -d DispatchLog -tAc "SELECT count(*) FROM relay_log;" 2>/dev/null | tr -d '[:space:]')
[[ "${OLD_ROWS:-0}" -ge 20 ]] || fail "expected at least 20 rows written by the old version, saw ${OLD_ROWS:-0}"
ok "$OLD_ROWS rows written by the released version"

# Proof this really is the old schema: pre-0.7 installs track versions in schema_version, not EF.
sudo -u postgres psql -d DispatchLog -tAc "SELECT count(*) FROM schema_version;" >/dev/null 2>&1 \
    && ok "source has a schema_version table - a genuine pre-EF schema" \
    || fail "the released version did not produce a pre-EF schema; this test would prove nothing"

say "4. Upgrade in place to this build - no flags, as an operator would"
sudo systemctl stop dispatch
mkdir -p "$WORK/new" && tar -xzf "$NEW_TARBALL" -C "$WORK/new"
new_dir="$(find "$WORK/new" -maxdepth 1 -mindepth 1 -type d | head -n1)"

# Again no flags at all - the whole question is what happens to an existing install when someone upgrades
# the ordinary way.
sudo "$new_dir/install.sh" >"$WORK/install-new.log" 2>&1 \
    || { tail -30 "$WORK/install-new.log"; fail "the upgrade install failed"; }
grep -q "keeping its configuration and database unchanged" "$WORK/install-new.log" \
    || fail "the upgrade did not report preserving the existing configuration"

NEW_CS="$(sudo sed -n 's/.*"DispatchLog"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' /var/lib/dispatch/appsettings.json | head -n1)"
[[ "$NEW_CS" == "$OLD_CS" ]] \
    || fail "the upgrade repointed the database. before: $OLD_CS  after: $NEW_CS"
ok "upgrade preserved the PostgreSQL connection string"

wait_healthy || fail "the upgraded service did not come up"
ok "upgraded service is running"

AFTER_UPGRADE=$(sudo -u postgres psql -d DispatchLog -tAc "SELECT count(*) FROM relay_log;" | tr -d '[:space:]')
[[ "$AFTER_UPGRADE" -eq "$OLD_ROWS" ]] || fail "history changed during the upgrade: $OLD_ROWS -> $AFTER_UPGRADE"
EF_ROWS=$(sudo -u postgres psql -d DispatchLog -tAc 'SELECT count(*) FROM "__EFMigrationsHistory";' | tr -d '[:space:]')
[[ "${EF_ROWS:-0}" -ge 1 ]] || fail "the pre-EF schema was not adopted during the upgrade"
ok "schema adopted in place; all $AFTER_UPGRADE rows intact"

authenticate
[[ "$(dget /api/auth/status | jq_ "str(d.get('needsSetup', False)).lower()")" == "false" ]] \
    || fail "the upgraded install is asking for first-run setup"
ok "same dashboard password still works"

say "5. Migrate onto bundled SQLite"
sudo systemctl stop dispatch
sudo env ConnectionStrings__DispatchLog="$OLD_CS" DISPATCH_KEY_DIR=/opt/dispatch \
    /opt/dispatch/current/Dispatch.Service migrate-database --to "Data Source=/var/lib/dispatch/dispatch.db" \
    >"$WORK/migrate.log" 2>&1 || { tail -20 "$WORK/migrate.log"; fail "migrate-database failed"; }
grep -q "Row counts verified" "$WORK/migrate.log" || fail "the migration did not report verified row counts"
ok "migrated ($(grep -oE 'total *[0-9,]+' "$WORK/migrate.log" | tail -1 | tr -s ' ') rows)"

sudo python3 - <<'PYEOF'
import json
p = "/var/lib/dispatch/appsettings.json"
cfg = json.load(open(p))
cfg["ConnectionStrings"]["DispatchLog"] = "Data Source=/var/lib/dispatch/dispatch.db"
json.dump(cfg, open(p, "w"), indent=2)
PYEOF
sudo chown dispatch:dispatch /var/lib/dispatch/appsettings.json

# The migration runs as root, so the file it creates is root-owned unless the migrator corrects it. The
# service runs as 'dispatch' and SQLite would then fail every write while reads kept working - which is
# exactly how this went unnoticed the first time. Assert the ownership rather than fixing it here: fixing
# it in the test would hide the very bug the test exists to catch.
DB_OWNER="$(sudo stat -c '%U' /var/lib/dispatch/dispatch.db)"
[[ "$DB_OWNER" == "dispatch" ]] \
    || fail "the migrated database is owned by '$DB_OWNER', not the service account - the service will fail every write"
ok "migrated database is owned by the service account"

sudo systemctl start dispatch
wait_healthy || fail "the service did not come up on SQLite"
ok "running on bundled SQLite"

say "6. Verify the customer's data made the whole journey"
authenticate
[[ "$(dget /api/auth/status | jq_ "str(d.get('needsSetup', False)).lower()")" == "false" ]] \
    || fail "the SQLite install is asking for first-run setup - config did not migrate"
ok "not treated as a fresh install"

MIGRATED=$(sudo -u dispatch sqlite3 /var/lib/dispatch/dispatch.db "SELECT count(*) FROM relay_log;" 2>/dev/null \
    || sudo python3 -c "import sqlite3;print(sqlite3.connect('/var/lib/dispatch/dispatch.db').execute('SELECT count(*) FROM relay_log').fetchone()[0])")
[[ "$MIGRATED" -eq "$OLD_ROWS" ]] || fail "mail history lost in the migration: $OLD_ROWS -> $MIGRATED"
ok "all $MIGRATED rows written by the OLD version survived onto SQLite"

dget '/api/messages?pageSize=50' | jq_ "'found' if any('old version' in str(r.get('subject','')) for r in d['rows']) else 'missing'" \
    | grep -q found || fail "messages sent on the released version are not in the log"
ok "mail sent on the released version is visible in the upgraded install"

NEW_KEY="$(create_key post-upgrade)"
curl -s -X POST "$API/api/v1/messages" -H "Authorization: Bearer $NEW_KEY" -H 'Content-Type: application/json' \
    -d '{"from":"new@local.test","to":["dest@local.test"],"subject":"sent after the upgrade","text":"body"}' >/dev/null
sleep 6
dget '/api/messages?pageSize=50' | jq_ "'found' if any('after the upgrade' in str(r.get('subject','')) for r in d['rows']) else 'missing'" \
    | grep -q found || fail "new mail does not flow after the upgrade"
ok "new mail flows, alongside the migrated history"

# A write that touches the database directly, not just the spool. Reads kept working when the file was
# root-owned, so only a write proves the service really owns its database.
dget '/api/keys' | jq_ "'found' if any(k.get('name') == 'post-upgrade' for k in d) else 'missing'" \
    | grep -q found || fail "the key created after the upgrade was not persisted"
ok "writes persist to the migrated database"

say "PASSED - $tag on PostgreSQL upgraded in place and migrated to bundled SQLite"
printf '   %s messages from the released version survived binaries, schema and engine changing\n\n' "$OLD_ROWS"
