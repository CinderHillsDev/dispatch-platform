#!/usr/bin/env bash
#
# Upgrade smoke test: an existing PostgreSQL install moves onto bundled SQLite.
#
# This is the 0.7 upgrade path for the one deployment in the field, exercised end to end against the real
# service rather than the migrator in isolation. DatabaseMigratorTests already proves rows and keys survive
# a copy; what this proves is the thing an operator actually does:
#
#   1. run the service on PostgreSQL and put real data through it (relay, API key, delivered mail)
#   2. stop it
#   3. Dispatch.Service migrate-database --to "Data Source=..."
#   4. repoint the connection string and start it again
#   5. everything still works, and history is still there
#
# The checks that matter are the ones that would fail SILENTLY:
#   * logging in with the SAME password proves the config table came across, bcrypt hash and all
#   * the migrated relay keeps its id, so historical mail stays attributed to it
#   * a NEW message after cutover proves key sequences advanced and do not collide with copied history
#   * the encrypted TLS-certificate password still decrypts, proving ciphertext survived untouched
#
# Usage:  tests/smoke/upgrade-postgres-to-sqlite.sh
# Requires: docker, dotnet, curl, python3. Leaves nothing running.

set -euo pipefail

readonly ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly WORK="$(mktemp -d)"
readonly PG_NAME="dispatch-upgrade-smoke"
readonly PG_PORT=55433
readonly PG_PASS="Upgrade_Smoke_Pass1"
readonly ADMIN_PW='Upgrade_Smoke_Adm1n!'
readonly DASH="https://localhost:8420"
readonly API="http://localhost:8025"
readonly PG_CS="Host=localhost;Port=${PG_PORT};Database=DispatchLog;Username=postgres;Password=${PG_PASS}"
readonly SQLITE_CS="Data Source=${WORK}/dispatch.db"

SERVICE_PID=""

cleanup() {
    [[ -n "$SERVICE_PID" ]] && kill "$SERVICE_PID" 2>/dev/null || true
    docker rm -f "$PG_NAME" >/dev/null 2>&1 || true
    rm -rf "$WORK"
}
trap cleanup EXIT

say()  { printf '\n\033[1m== %s\033[0m\n' "$*"; }
ok()   { printf '   \033[32mok\033[0m   %s\n' "$*"; }
fail() { printf '   \033[31mFAIL\033[0m %s\n' "$*" >&2; exit 1; }

# --- service lifecycle ---------------------------------------------------------------------------
# DISPATCH_KEY_DIR is deliberately the SAME across both runs. The at-rest encryption key is not in the
# database, so a migration that changed it would leave every encrypted setting unreadable - this is the
# condition the CLI warns about, held constant here because we are migrating in place.
start_service() {
    local cs="$1" label="$2"
    ConnectionStrings__DispatchLog="$cs" \
    AdminPassword="$ADMIN_PW" \
    DISPATCH_KEY_DIR="$WORK/keys" \
    DISPATCH_LOG_DIR="$WORK/logs" \
        dotnet run --project "$ROOT/src/Dispatch.Service" --no-build \
        >"$WORK/service-$label.log" 2>&1 &
    SERVICE_PID=$!

    for _ in $(seq 1 60); do
        if curl -sk --max-time 2 "$DASH/health" >/dev/null 2>&1; then
            ok "service is up on $label"
            return 0
        fi
        kill -0 "$SERVICE_PID" 2>/dev/null || { tail -30 "$WORK/service-$label.log"; fail "service exited during startup on $label"; }
        sleep 2
    done
    tail -30 "$WORK/service-$label.log"
    fail "service did not become healthy on $label"
}

stop_service() {
    [[ -z "$SERVICE_PID" ]] && return 0
    kill "$SERVICE_PID" 2>/dev/null || true
    wait "$SERVICE_PID" 2>/dev/null || true
    SERVICE_PID=""
    ok "service stopped"
}

# --- dashboard helpers ---------------------------------------------------------------------------
readonly JAR="$WORK/cookies"
dget()  { curl -sk -b "$JAR" -c "$JAR" "$DASH$1"; }
# X-Dispatch-Request is the CSRF guard (WebAuthMiddleware.CsrfHeader): the dashboard API rejects any
# mutating request without it with a bodyless 403, so every POST must carry it.
dpost() { curl -sk -b "$JAR" -c "$JAR" -H 'Content-Type: application/json' -H 'X-Dispatch-Request: 1' -X POST -d "$2" "$DASH$1"; }

# Extracts a value from a JSON response. On malformed input it prints the raw body and the request that
# produced it - an HTML error page or an empty 401 is far more useful than a JSON decode traceback.
jq_() {
    local expr="$1" label="${2:-response}" body
    body="$(cat)"
    python3 -c "
import sys, json
raw = sys.stdin.read()
try:
    d = json.loads(raw)
except Exception:
    sys.stderr.write('   raw %s: %s\n' % ('$label', raw[:400] if raw.strip() else '(empty)'))
    sys.exit(3)
print($expr)
" <<<"$body"
}

authenticate() {
    rm -f "$JAR"
    local needs
    needs=$(dget /api/auth/status | jq_ "str(d.get('needsSetup', False)).lower()")
    if [[ "$needs" == "true" ]]; then
        dpost /api/auth/password "{\"password\":\"$ADMIN_PW\"}" >/dev/null
        ok "first-run password set"
    else
        dpost /api/auth/login "{\"password\":\"$ADMIN_PW\"}" >/dev/null
        ok "logged in with the existing password"
    fi
}

# ==================================================================================================
say "1. Start PostgreSQL and build"
docker rm -f "$PG_NAME" >/dev/null 2>&1 || true
docker run -d --rm --name "$PG_NAME" -e POSTGRES_PASSWORD="$PG_PASS" -p "${PG_PORT}:5432" postgres:17 >/dev/null
for _ in $(seq 1 40); do docker exec "$PG_NAME" pg_isready -U postgres >/dev/null 2>&1 && break; sleep 2; done
ok "postgres ready on $PG_PORT"
dotnet build "$ROOT/Dispatch.slnx" -v q --nologo >/dev/null
ok "built"

say "2. Run Dispatch on PostgreSQL and put real data through it"
start_service "$PG_CS" postgres
authenticate

RELAY_ID=$(dpost /api/relays '{"name":"upgrade-smoke","provider":"Local"}' | jq_ "d['id']")
dpost "/api/relays/$RELAY_ID/set-default" '{}' >/dev/null
ok "relay $RELAY_ID created and set default"

API_KEY=$(dpost /api/keys '{"name":"upgrade-smoke","rateLimitPerMinute":0}' | jq_ "d['key']")
ok "api key issued"

for i in $(seq 1 25); do
    curl -s -X POST "$API/api/v1/messages" -H "Authorization: Bearer $API_KEY" \
        -H 'Content-Type: application/json' \
        -d "{\"from\":\"pre@local.test\",\"to\":[\"dest@local.test\"],\"subject\":\"pre-migration $i\",\"text\":\"body $i\"}" \
        >/dev/null
done
sleep 5
BEFORE_COUNT=$(dget '/api/messages?limit=200' | jq_ "len(d['rows'])")
[[ "$BEFORE_COUNT" -ge 25 ]] || fail "expected at least 25 messages before migration, saw $BEFORE_COUNT"
ok "$BEFORE_COUNT messages logged on postgres"

# The TLS certificate password is stored ENCRYPTED in the config table. /api/settings surfaces the cert
# state the service resolved at startup, which it can only do by decrypting that value - so comparing it
# either side of the migration proves ciphertext crossed untouched and still decrypts with the same key.
BEFORE_TLS=$(dget /api/settings | jq_ "json.dumps(d.get('tls', d.get('webUi', {})), sort_keys=True)")
ok "tls state captured before migration"

say "3. Stop the service (as an operator would) and migrate"
stop_service

ConnectionStrings__DispatchLog="$PG_CS" DISPATCH_KEY_DIR="$WORK/keys" \
    dotnet run --project "$ROOT/src/Dispatch.Service" --no-build -- \
    migrate-database --to "$SQLITE_CS" 2>&1 | grep -vE '^\s*(info|dbug|warn):' | sed 's/^/   /'

[[ -f "$WORK/dispatch.db" ]] || fail "no sqlite database was produced"
ok "migrated to $(du -h "$WORK/dispatch.db" | cut -f1) sqlite file"

say "4. Restart on SQLite and verify the upgrade"
start_service "$SQLITE_CS" sqlite

# The password came across in the config table, hash and all. A fresh install would demand setup instead.
NEEDS_SETUP=$(dget /api/auth/status | jq_ "str(d.get('needsSetup', False)).lower()")
[[ "$NEEDS_SETUP" == "false" ]] || fail "sqlite install is asking for first-run setup - the config table did not migrate"
ok "not treated as a fresh install"
authenticate

AFTER_COUNT=$(dget '/api/messages?limit=200' | jq_ "len(d['rows'])")
[[ "$AFTER_COUNT" -eq "$BEFORE_COUNT" ]] || fail "message history changed across the migration: $BEFORE_COUNT -> $AFTER_COUNT"
ok "all $AFTER_COUNT messages still present"

MIGRATED_RELAY=$(dget /api/relays | jq_ "[r['id'] for r in d if r['name']=='upgrade-smoke'][0]")
[[ "$MIGRATED_RELAY" -eq "$RELAY_ID" ]] || fail "relay id changed ($RELAY_ID -> $MIGRATED_RELAY); historical attribution is broken"
ok "relay kept id $RELAY_ID, so historical mail stays attributed"

AFTER_TLS=$(dget /api/settings | jq_ "json.dumps(d.get('tls', d.get('webUi', {})), sort_keys=True)")
[[ "$AFTER_TLS" == "$BEFORE_TLS" ]] || fail "tls settings changed across the migration:\n  before: $BEFORE_TLS\n  after:  $AFTER_TLS"
ok "encrypted settings still decrypt after the move"

# A new message must not collide with a copied primary key - the failure mode if identity sequences
# were left pointing at 1 rather than at the end of the copied data.
NEW_KEY=$(dpost /api/keys '{"name":"post-migration","rateLimitPerMinute":0}' | jq_ "d['key']")
curl -s -X POST "$API/api/v1/messages" -H "Authorization: Bearer $NEW_KEY" \
    -H 'Content-Type: application/json' \
    -d '{"from":"post@local.test","to":["dest@local.test"],"subject":"post-migration","text":"after cutover"}' >/dev/null
sleep 5

FINAL_COUNT=$(dget '/api/messages?limit=200' | jq_ "len(d['rows'])")
[[ "$FINAL_COUNT" -gt "$AFTER_COUNT" ]] || fail "new message after cutover did not appear (count stayed $AFTER_COUNT)"
dget '/api/messages?limit=200' | jq_ "'post-migration' if any(r.get('subject')=='post-migration' for r in d['rows']) else ''" | grep -q post-migration \
    || fail "the post-migration message is missing from the log"
ok "new mail flows and lands beside the migrated history"

say "PASSED - postgres -> sqlite upgrade is clean"
printf '   %s messages migrated, relay id preserved, encrypted config intact, new mail delivering\n\n' "$BEFORE_COUNT"
