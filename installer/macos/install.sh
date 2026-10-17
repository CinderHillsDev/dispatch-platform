#!/usr/bin/env bash
#
# Dispatch SMTP Relay - macOS installer.
#
# Installs the service, lays out the config/data directories, and registers it with launchd. This is the
# macOS counterpart of installer/linux/install.sh: same flags, same on-disk shape (binaries under
# /opt/dispatch/releases/<ver> with /opt/dispatch/current pointing at the active release; appsettings.json
# and the spool under the data dir), different service manager (launchd instead of systemd).
#
# Two deliberate differences from Linux, both of which change what the operator gets:
#
#   * Privileged ports. Linux runs the service as an unprivileged user and grants CAP_NET_BIND_SERVICE.
#     macOS has no POSIX capabilities and no equivalent per-process grant, so there is no way to let a
#     non-root process bind 25/587. Default: the daemon runs as root, and only for that reason. Prefer an
#     unprivileged service? Pass --user <name>; the SMTP listener then cannot bind 25/587 and falls back to
#     2525 automatically, exactly as it does on a Linux box where the capability is missing. Nothing else
#     changes, and the dashboard can move the ports later.
#   * No account creation. macOS has no useradd; the only tool is dscl, and silently creating a local
#     account on somebody's machine is not something an installer should do. --user must name an account
#     that already exists (see --user below for the one-liner to create one).
#
# Gatekeeper: a self-contained .NET binary downloaded by a browser carries the com.apple.quarantine
# attribute, and the first exec is refused. This script strips it from the installed binaries (it runs as
# root and the operator just told it to install this build). Binaries built from source on this machine
# never had the attribute.
#
# Usage:
#   # Default: bundled SQLite. No database server to install, configure, back up or patch.
#   sudo ./install.sh --admin-password "<pw>" [--generate-cert]
#
#   # Or point at a database server you already run (PostgreSQL, MariaDB/MySQL, SQL Server):
#   sudo ./install.sh --sql-connection "Host=...;Database=DispatchLog;Username=...;Password=..." --admin-password "<pw>"
#
#   # From the release bundle (self-contained Apple Silicon binaries; no .NET SDK / Node required):
#   sudo ./install.sh --prebuilt ./bin-arm64 --admin-password "<pw>"
#
#   # Run unprivileged (SMTP lands on 2525 because 25/587 need root):
#   sudo ./install.sh --user _dispatch --admin-password "<pw>"
#
#   # Remove the daemon and the binaries, keeping data + logs:
#   sudo ./install.sh --uninstall
#
# Dispatch does NOT install a database server. Either it uses the bundled SQLite file, or you point it at
# a server you already run and support.
#
# Flags:
#   --sql-connection <s>   Connection string for a database server you already run. Omit for bundled SQLite.
#   --db-provider <name>   Sqlite | Postgres | SqlServer | MySql. Only needed when the connection string is
#                          ambiguous - "Server=...;Database=...;User Id=..." is valid for both SQL Server
#                          and MySQL/MariaDB, and Dispatch refuses to guess there.
#   --admin-password <s>   Dashboard admin password seed (prompted if omitted).
#   --generate-cert        Generate a self-signed PFX and serve the dashboard over HTTPS (spec §17.2).
#   --http-port <n>        Dashboard port, recorded so the updater knows where to poll /health (default 8420).
#   --api-port <n>         Firewall ingestion API port (default 8025).
#   --smtp-ports <a,b>     Firewall SMTP ports (default 25,587; the listener falls back to 2525 if 25 is taken).
#   --user <name>          Run the daemon as this existing account instead of root (SMTP then falls back to
#                          2525). The updater still runs as root - it has to swap files under /opt.
#   --source <path>        Repo source root (default: two levels up from this script). Build-from-source mode.
#   --prebuilt <dir>       Install pre-published self-contained binaries from <dir> instead of building from
#                          source. Used by the release bundle; needs neither the .NET SDK nor Node.
#   --no-firewall          Skip the best-effort Application Firewall rules.
#   --no-start             Stage the install (binaries + LaunchDaemons installed) without starting anything.
#                          launchd loads both jobs at the next boot.
#   --uninstall            Boot out both daemons, remove the property lists and /opt/dispatch, then exit.
#                          /var/db/dispatch and /var/log/dispatch are left in place (say so at the end).
#   --status               Report the daemon state, the dashboard, the listening SMTP ports, the spool
#                          counts and the active release. Needs no install, no app, no browser.
#   --stop                 Boot the daemon out (a KeepAlive job ignores `launchctl stop`). The update
#                          watcher stays loaded; mail then queues at the sender until --start.
#   --start                Enable + bootstrap the daemon, clearing any persisted disabled override.
#
set -euo pipefail

INSTALL_DIR="/opt/dispatch"
# macOS convention for service state is /var/db (Linux uses /var/lib). This is the service's content root:
# appsettings.json, dispatch.db, the spool, .dispatch-key and the updates/ staging area all live here.
DATA_DIR="/var/db/dispatch"
CONFIG_DIR="$DATA_DIR"
LOG_DIR="/var/log/dispatch"
LABEL="app.dispatchrelay.dispatch"
UPDATE_LABEL="app.dispatchrelay.dispatch-update"
DAEMON_DIR="/Library/LaunchDaemons"
HTTP_PORT="8420"
API_PORT="8025"
SMTP_PORTS="25,587"
SQL_CONNECTION=""
DB_PROVIDER=""
ADMIN_PASSWORD=""
SOURCE_DIR=""
PREBUILT_DIR=""
SERVICE_USER=""
GENERATE_CERT="0"
TLS_CERT_PATH=""
TLS_CERT_PASSWORD=""
NO_FIREWALL="0"
NO_START="0"
ACTION=""
UNINSTALL="0"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --sql-connection) SQL_CONNECTION="$2"; shift 2;;
    --db-provider) DB_PROVIDER="$2"; shift 2;;
    --admin-password) ADMIN_PASSWORD="$2"; shift 2;;
    --generate-cert) GENERATE_CERT="1"; shift;;
    --http-port) HTTP_PORT="$2"; shift 2;;
    --api-port) API_PORT="$2"; shift 2;;
    --smtp-ports) SMTP_PORTS="$2"; shift 2;;
    --user) SERVICE_USER="$2"; shift 2;;
    --source) SOURCE_DIR="$2"; shift 2;;
    --prebuilt) PREBUILT_DIR="$2"; shift 2;;
    --no-firewall) NO_FIREWALL="1"; shift;;
    # Stage the install without starting the service now (the daemon is installed, and launchd brings it up
    # at the next boot). Used when baking an image, where the service should start on first boot.
    --no-start) NO_START="1"; shift;;
    --uninstall) UNINSTALL="1"; shift;;
    --status) ACTION="status"; shift;;
    --start) ACTION="start"; shift;;
    --stop) ACTION="stop"; shift;;
    --help|-h)
      # Print this file's own flag list rather than a second copy of it, which is how help text drifts.
      sed -n '/^# Flags:/,/^#$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      exit 0;;
    *) echo "Unknown option: $1" >&2; exit 1;;
  esac
done

[[ $EUID -eq 0 ]] || { echo "Please run as root (sudo)." >&2; exit 1; }
[[ "$(uname -s)" == "Darwin" ]] || { echo "This installer is for macOS (found: $(uname -s)). Use installer/linux/install.sh." >&2; exit 1; }

# Uninstall first, before anything can fail for reasons that only matter to an install. Removes the daemons
# and the program files; the database, config, spool and key file are NOT touched, because deleting an
# operator's mail history is not a side effect someone expects from "uninstall" (same policy as Linux).
if [[ "$UNINSTALL" == "1" ]]; then
  echo "==> Removing the launchd daemons"
  # bootout = stop + unload in one step (there is no separate `launchctl stop` that works on a KeepAlive
  # job). Best-effort: on a partially-installed box one or both may already be gone.
  launchctl bootout "system/${LABEL}" >/dev/null 2>&1 || true
  launchctl bootout "system/${UPDATE_LABEL}" >/dev/null 2>&1 || true
  launchctl disable "system/${LABEL}" >/dev/null 2>&1 || true
  launchctl disable "system/${UPDATE_LABEL}" >/dev/null 2>&1 || true
  rm -f "$DAEMON_DIR/$LABEL.plist" "$DAEMON_DIR/$UPDATE_LABEL.plist"
  # Drop the Application Firewall entry before the binary it names disappears.
  if [[ -x /usr/libexec/ApplicationFirewall/socketfilterfw ]]; then
    /usr/libexec/ApplicationFirewall/socketfilterfw --remove "$INSTALL_DIR/current/Dispatch.Service" >/dev/null 2>&1 || true
  fi
  echo "==> Removing $INSTALL_DIR"
  rm -rf "$INSTALL_DIR"
  echo
  echo "Dispatch is uninstalled. Kept on disk (remove them yourself if you are sure):"
  echo "  $DATA_DIR   - config, database, spool, .dispatch-key"
  echo "  $LOG_DIR          - logs"
  exit 0
fi

# Lifecycle control, here so the machine always carries its own on/off switch.
#
# This script is copied to /opt/dispatch/install.sh at install time, so `--status`, `--start` and `--stop`
# work from the box, over SSH, from a cron job, or from a Recovery terminal - with no browser, and with no
# dependency on anything in /Applications. That dependency is the thing to avoid: a menu bar app can be
# dragged to the Trash, moved to another volume, or deleted by the next macOS cleanup prompt, and none of
# those actions should leave a root process listening on SMTP 25 that nobody can name. The app wraps exactly
# these operations; it is not the management surface.
if [[ -n "$ACTION" ]]; then
  jval() { printf '%s' "$1" | tr -d ' \n' | grep -o "\"$2\":\"[^\"]*\"" | head -1 | cut -d'"' -f4; }
  jnum() { printf '%s' "$1" | tr -d ' \n' | grep -o "\"$2\":[0-9]*"  | head -1 | cut -d':' -f2; }
  # The port the applier was told to poll, which is the port the dashboard answers on. Read back from the
  # installed watcher plist rather than trusting this invocation's default: an operator who moved the
  # dashboard with --http-port at install time would otherwise be told "nothing is listening on 8420".
  HEALTH_PORT="$(plutil -extract EnvironmentVariables.DISPATCH_HEALTH_PORT raw \
      "$DAEMON_DIR/$UPDATE_LABEL.plist" 2>/dev/null || true)"
  [[ -n "$HEALTH_PORT" ]] || HEALTH_PORT="$HTTP_PORT"

  case "$ACTION" in
    status)
      if [[ ! -f "$DAEMON_DIR/$LABEL.plist" ]]; then
        echo "Dispatch: not installed (no $DAEMON_DIR/$LABEL.plist)"
        exit 3
      fi
      lp="system/$LABEL"
      # launchctl prints these as "key = value" with the delimiter as its own field, so $2 is "=" and a
      # naive awk '{print $2}' renders pid as "pid==". "last exit code = (never exited)" also has a space
      # in it, so take everything after the delimiter rather than a single field.
      launchd_dump="$(launchctl print "$lp" 2>/dev/null || true)"
      state="$(printf '%s\n' "$launchd_dump" | awk '/^[[:space:]]*state = /{sub(/^[^=]*= /,""); print; exit}')"
      pid="$(printf '%s\n' "$launchd_dump" | awk '/^[[:space:]]*pid = /{sub(/^[^=]*= /,""); print; exit}')"
      xcode="$(printf '%s\n' "$launchd_dump" | awk '/last exit code =/{sub(/^[^=]*= /,""); print; exit}')"
      printf 'Dispatch: state=%s' "${state:-not loaded}"
      [[ -n "$pid" && "$pid" != "0" ]] && printf ' pid=%s' "$pid"
      [[ -n "$xcode" ]] && printf ' last-exit=%s' "$xcode"
      printf '\n'
      if [[ -n "$xcode" && "$xcode" != "(never exited)" && "${xcode%% *}" != "0" ]]; then
        echo "           the last run exited with $xcode - read $LOG_DIR/launchd.err.log"
      fi
      if json="$(curl -fsSk -m 4 "https://localhost:${HEALTH_PORT}/health" 2>/dev/null)"; then
        echo "  dashboard  https://localhost:${HEALTH_PORT} ($(jval "$json" status))"
        echo "  version    $(jval "$json" version)"
        echo "  smtp       listening on $(printf '%s' "$json" | tr -d ' \n' \
              | grep -o '"listeningPorts":\[[^]]*\]' | sed 's/.*\[//; s/\]//' | tr -d '"')"
        echo "  spool      incoming=$(jnum "$json" incoming) processing=$(jnum "$json" processing) failed=$(jnum "$json" failed)"
      else
        echo "  dashboard  https://localhost:${HEALTH_PORT} is not answering /health"
        echo "             last daemon errors: ${LOG_DIR}/launchd.err.log"
      fi
      rel="$(cd "$INSTALL_DIR/current" 2>/dev/null && pwd -P)"
      echo "  release    ${rel:-(no current symlink under $INSTALL_DIR)}"
      echo "  data       $DATA_DIR"
      ;;
    stop)
      # bootout, not `launchctl stop`: a KeepAlive job is restarted immediately by anything short of that.
      # The update watcher is deliberately left loaded - it does nothing without an apply.request, and
      # stopping it would silently disable staged updates until the next start.
      launchctl bootout "system/$LABEL" >/dev/null 2>&1 || true
      echo "Dispatch stopped. It starts again at the next boot, or with: sudo $0 --start"
      echo "  Mail sent to 25/587 meanwhile is refused, so senders queue and retry."
      ;;
    start)
      launchctl enable "system/$LABEL" >/dev/null 2>&1 || true
      launchctl bootstrap system "$DAEMON_DIR/$LABEL.plist" >/dev/null 2>&1 \
        || launchctl kickstart -k "system/$LABEL" >/dev/null 2>&1 || true
      echo "Dispatch started. Status: sudo $0 --status"
      ;;
  esac
  exit 0
fi

# The daemon user. Empty means "run as root" (the default, and the only way to bind 25/587 on macOS).
OWNER="root"
OWNER_GROUP="wheel"
if [[ -n "$SERVICE_USER" ]]; then
  id -u "$SERVICE_USER" >/dev/null 2>&1 || {
    echo "No such account: $SERVICE_USER" >&2
    echo "Create one with (a hidden, non-login service account):" >&2
    echo "  sudo dscl . -create /Users/$SERVICE_USER UniqueID 451 && \\" >&2
    echo "  sudo dscl . -create /Users/$SERVICE_USER PrimaryGroupID -2 && \\" >&2
    echo "  sudo dscl . -create /Users/$SERVICE_USER Shell /usr/bin/false && \\" >&2
    echo "  sudo dscl . -create /Users/$SERVICE_USER IsHidden 1" >&2
    echo "or install without --user to run as root." >&2
    exit 1
  }
  OWNER="$SERVICE_USER"
  OWNER_GROUP="$(id -gn "$SERVICE_USER" 2>/dev/null || echo wheel)"
fi

# ---- Optional: generate a self-signed TLS cert for the dashboard --------------------------------
generate_cert() {
  command -v openssl >/dev/null 2>&1 || { echo "openssl is required for --generate-cert (install Xcode Command Line Tools, or brew install openssl)." >&2; exit 1; }
  echo "==> Generating a self-signed TLS certificate for the dashboard"
  mkdir -p "$CONFIG_DIR"
  TLS_CERT_PASSWORD="$(openssl rand -base64 24)"
  TLS_CERT_PATH="$CONFIG_DIR/dispatch.pfx"
  local tmp; tmp="$(mktemp -d)"
  openssl req -x509 -newkey rsa:2048 -nodes -days 825 -subj "/CN=$(hostname -f 2>/dev/null || hostname)" \
    -keyout "$tmp/key.pem" -out "$tmp/cert.pem" >/dev/null 2>&1
  openssl pkcs12 -export -out "$TLS_CERT_PATH" -inkey "$tmp/key.pem" -in "$tmp/cert.pem" -passout "pass:${TLS_CERT_PASSWORD}" >/dev/null 2>&1
  rm -rf "$tmp"
  chmod 600 "$TLS_CERT_PATH"
}

# An upgrade must never repoint the database.
#
# This script rewrites appsettings.json wholesale, and appsettings holds exactly two things: the database
# connection string and the Web UI TLS certificate. So on an existing install with no --sql-connection, the
# file is left completely alone rather than parsed and rebuilt. Without that, an operator upgrading in
# place - the normal way, with no flags - would silently be moved onto a brand-new empty SQLite file: the
# service would start, look healthy, show no mail history and ask for first-run setup, while every message
# they had ever relayed sat untouched in the database they were using. Nothing would report an error.
#
# Not parsing the old file is deliberate. Reading a connection string back out of JSON with shell tools
# means getting quote escaping right in a password, and getting it subtly wrong is the same silent failure
# by another route. Preserving the file byte for byte cannot be subtly wrong.
PRESERVE_CONFIG="0"
if [[ -z "$SQL_CONNECTION" && -f "$CONFIG_DIR/appsettings.json" ]]; then
  PRESERVE_CONFIG="1"
  echo "==> Existing install detected; keeping its configuration and database unchanged"
  if [[ "$GENERATE_CERT" == "1" ]]; then
    echo "    NOTE: --generate-cert was ignored because the existing configuration was preserved."
    echo "          Set the dashboard certificate under Settings -> Connections -> TLS certificate."
    GENERATE_CERT="0"
  fi
elif [[ -z "$SQL_CONNECTION" ]]; then
  SQL_CONNECTION="Data Source=${DATA_DIR}/dispatch.db"
  echo "==> Using the bundled SQLite database at ${DATA_DIR}/dispatch.db"
else
  echo "==> Using the database server from --sql-connection"
fi

# Prompt for the admin password only when omitted AND running interactively, so a non-interactive run
# (piped, or baked into an image) never hangs on a missing TTY. The dashboard requires the admin password
# to be set on first login either way, so an empty seed is safe.
if [[ -z "$ADMIN_PASSWORD" && -t 0 ]]; then
  read -rsp "Set the dashboard admin password: " ADMIN_PASSWORD; echo
  [[ -n "$ADMIN_PASSWORD" ]] || { echo "Admin password is required." >&2; exit 1; }
fi

[[ "$GENERATE_CERT" == "1" ]] && generate_cert

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Auto-select prebuilt binaries by CPU arch when --prebuilt wasn't given, so one command works whether this
# script is being run out of the release bundle, a mounted .dmg, or a source tree. Releases ship bin-arm64
# only; a machine that lands on bin-x64 has no artifact to find, so the per-arch lookup stays for anyone
# publishing their own payload. If nothing matches we fall through to build-from-source.
if [[ -z "$PREBUILT_DIR" ]]; then
  case "$(uname -m)" in
    arm64|aarch64) _arch=arm64 ;;
    x86_64)        _arch=x64 ;;
    *)             _arch="" ;;
  esac
  if [[ -n "$_arch" && -d "$SCRIPT_DIR/bin-$_arch" ]]; then PREBUILT_DIR="$SCRIPT_DIR/bin-$_arch"
  elif [[ -d "$SCRIPT_DIR/bin" ]]; then PREBUILT_DIR="$SCRIPT_DIR/bin"
  fi
  [[ -n "$PREBUILT_DIR" ]] && echo "==> Using prebuilt binaries for $(uname -m): $PREBUILT_DIR"
fi

# Stop the running daemon first: its binary is the file we are about to replace, and launchd will happily
# restart a crashing job forever while we try to overwrite it. Best-effort on a fresh install.
launchctl bootout "system/${LABEL}" >/dev/null 2>&1 || true

# Binaries live under /opt/dispatch/releases/<ver> with /opt/dispatch/current symlinked to the active one,
# so the web-UI updater can swap releases atomically by re-pointing 'current' (and roll back the same way).
mkdir -p "$INSTALL_DIR/releases"
REL_VER="base"
if [[ -n "$PREBUILT_DIR" ]]; then
  # Prebuilt mode: copy the self-contained publish output as-is (no SDK / Node build).
  # A relative --prebuilt is resolved against the current dir, then against the script's own dir, so
  # `--prebuilt ./bin-arm64` works whether you run it from inside the extracted folder or by path.
  if [[ ! -d "$PREBUILT_DIR" && -d "$SCRIPT_DIR/$PREBUILT_DIR" ]]; then PREBUILT_DIR="$SCRIPT_DIR/$PREBUILT_DIR"; fi
  [[ -d "$PREBUILT_DIR" ]] || { echo "--prebuilt dir not found: $PREBUILT_DIR (cwd: $PWD, script: $SCRIPT_DIR)" >&2; exit 1; }
  [[ -f "$PREBUILT_DIR/Dispatch.Service" ]] || { echo "--prebuilt dir has no Dispatch.Service executable: $PREBUILT_DIR" >&2; exit 1; }
  [[ -f "$PREBUILT_DIR/.dispatch-version" ]] && REL_VER="$(tr -d '[:space:]' < "$PREBUILT_DIR/.dispatch-version")"
  # Nothing to do for runtime dependencies: unlike a minimal Linux image, macOS already has the ICU the
  # self-contained runtime binds to and the TLS trust store, so there is no package to install.
  REL_DIR="$INSTALL_DIR/releases/$REL_VER"
  echo "==> Installing pre-built binaries from $PREBUILT_DIR to $REL_DIR"
  rm -rf "$REL_DIR"; mkdir -p "$REL_DIR"
  cp -R "$PREBUILT_DIR/." "$REL_DIR/"
  chmod +x "$REL_DIR/Dispatch.Service"
else
  # Build-from-source mode: needs the .NET SDK + Node.
  SOURCE_DIR="${SOURCE_DIR:-$(cd "$SCRIPT_DIR/../.." && pwd)}"
  echo "==> Building the web UI"
  ( cd "$SOURCE_DIR/src/Dispatch.UI" && npm ci && npm run build )
  rm -rf "$SOURCE_DIR/src/Dispatch.Web/wwwroot"
  mkdir -p "$SOURCE_DIR/src/Dispatch.Web/wwwroot"
  cp -R "$SOURCE_DIR/src/Dispatch.UI/dist/." "$SOURCE_DIR/src/Dispatch.Web/wwwroot/"

  REL_DIR="$INSTALL_DIR/releases/$REL_VER"
  echo "==> Publishing the service to $REL_DIR"
  rm -rf "$REL_DIR"; mkdir -p "$REL_DIR"
  dotnet publish "$SOURCE_DIR/src/Dispatch.Service" -c Release -o "$REL_DIR"
fi

# Point 'current' at this release, then clear any old flat-layout binaries so only the symlinked release
# is ever run.
ln -sfn "$REL_DIR" "$INSTALL_DIR/current"
rm -f "$INSTALL_DIR/Dispatch.Service" 2>/dev/null || true
find "$INSTALL_DIR" -maxdepth 1 -type f -name '*.dll' -delete 2>/dev/null || true

# Arm64 macOS refuses to execute an unsigned Mach-O, and every binary we ship has to carry at least an
# ad-hoc signature. Release payloads are signed at build time; a from-source build is signed by the SDK on
# the machine. Re-signing the apphost here is cheap, idempotent insurance against a copy that lost it (and
# against a payload that was downloaded and had its signature invalidated by any byte change).
if command -v codesign >/dev/null 2>&1; then
  codesign --force --sign - "$REL_DIR/Dispatch.Service" >/dev/null 2>&1 || \
    echo "WARN: could not ad-hoc sign the apphost - if the service is killed on start, run: sudo codesign --force --sign - $REL_DIR/Dispatch.Service" >&2
fi

# Strip the browser's quarantine flag from the installed payload, or the first exec is blocked.
xattr -dr com.apple.quarantine "$REL_DIR" >/dev/null 2>&1 || true

echo "==> Creating directories (owner: $OWNER)"
mkdir -p "$CONFIG_DIR" "$DATA_DIR/.dispatch-spool" "$LOG_DIR"

echo "==> Writing $CONFIG_DIR/appsettings.json"
# Spec §12.1: appsettings holds ONLY the connection string, the admin-password seed, and the Web UI TLS
# cert. Ports/spool/retry/etc. are seeded into the config table on first run and managed in the dashboard.
# Every value below is JSON-escaped via json_escape: the admin password (and, in theory, the connection
# string) is user-supplied, and a backslash, double quote or control character would otherwise produce
# invalid JSON - which makes the service throw on startup and never come up. Escape order matters:
# backslash first (so we don't double-escape the escapes we add), then quote, then the control chars.
json_escape() {
  local s=$1
  s=${s//\\/\\\\}
  s=${s//\"/\\\"}
  s=${s//$'\n'/\\n}
  s=${s//$'\r'/\\r}
  s=${s//$'\t'/\\t}
  printf '%s' "$s"
}
SQL_CONNECTION_J="$(json_escape "$SQL_CONNECTION")"
DB_PROVIDER_J=""
[[ -n "$DB_PROVIDER" ]] && DB_PROVIDER_J=",
  \"Database\": { \"Provider\": \"$(json_escape "$DB_PROVIDER")\" }"
ADMIN_PASSWORD_J="$(json_escape "$ADMIN_PASSWORD")"
WEBUI_TLS=""
if [[ -n "$TLS_CERT_PATH" ]]; then
  TLS_CERT_PATH_J="$(json_escape "$TLS_CERT_PATH")"
  TLS_CERT_PASSWORD_J="$(json_escape "$TLS_CERT_PASSWORD")"
  WEBUI_TLS=",
  \"WebUi\": { \"TlsCertPath\": \"${TLS_CERT_PATH_J}\", \"TlsCertPassword\": \"${TLS_CERT_PASSWORD_J}\" }"
fi
if [[ "$PRESERVE_CONFIG" == "1" ]]; then
  echo "==> Preserved $CONFIG_DIR/appsettings.json (connection string and TLS settings unchanged)"
else
cat > "$CONFIG_DIR/appsettings.json" <<JSON
{
  "ConnectionStrings": { "DispatchLog": "${SQL_CONNECTION_J}" }${DB_PROVIDER_J},
  "AdminPassword": "${ADMIN_PASSWORD_J}",
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } }${WEBUI_TLS}
}
JSON
fi

# The admin password is consumed once on first start: hashed into the database, then the plaintext seed is
# wiped from appsettings.json by the service so the password lives only in the database (encrypted, like
# every other secret in that file). The file itself is restricted below.
chown -R "$OWNER:$OWNER_GROUP" "$INSTALL_DIR" "$DATA_DIR" "$LOG_DIR" "$CONFIG_DIR"
chmod 700 "$DATA_DIR" "$CONFIG_DIR" "$LOG_DIR"
chmod 600 "$CONFIG_DIR/appsettings.json"
[[ -n "$TLS_CERT_PATH" ]] && chown "$OWNER:$OWNER_GROUP" "$TLS_CERT_PATH"

# Spool file security (spec §14.5): pre-create the subdirectories mode 700 so they're correct from first
# start; the daemon runs with Umask 0077 so every .eml/.meta is created rw------- (600).
mkdir -p "$DATA_DIR/.dispatch-spool/incoming" "$DATA_DIR/.dispatch-spool/processing" "$DATA_DIR/.dispatch-spool/failed"
chown -R "$OWNER:$OWNER_GROUP" "$DATA_DIR/.dispatch-spool"
chmod 700 "$DATA_DIR/.dispatch-spool" "$DATA_DIR/.dispatch-spool/incoming" "$DATA_DIR/.dispatch-spool/processing" "$DATA_DIR/.dispatch-spool/failed"

echo "==> Installing the launchd daemons + the web-UI updater"
# The plists and the applier live next to this script, in both the release bundle and the source tree
# (installer/macos/). Check rather than guess: a missing file here means launchd never gets a job and the
# symptom shows up much later as "the update did nothing".
PLIST_SRC="$SCRIPT_DIR"
for f in "$LABEL.plist" "$UPDATE_LABEL.plist" dispatch-update.sh; do
  [[ -f "$PLIST_SRC/$f" ]] || { echo "ERROR: $f not found next to install.sh ($PLIST_SRC)" >&2; exit 1; } 1>&2
done
# The templates carry fixed absolute paths plus two markers sed replaces: the optional UserName key, and
# the port the updater polls for health. Generated to a temp file so a bad substitution can never install a
# half-written daemon.
gen_plist() {
  local in="$1" out="$2" user_line=""
  # One line, not two: BSD sed cannot take a literal newline inside a replacement, and XML does not care.
  [[ -n "$SERVICE_USER" ]] && user_line="<key>UserName</key><string>${SERVICE_USER}</string>"
  sed -e "s|<!-- __USER__ -->|${user_line}|" -e "s|__HEALTH_PORT__|${HTTP_PORT}|g" "$in" > "$out"
  # A plist launchd cannot parse fails with a confusing "Bootstrap failed: 5", so say so here instead.
  plutil -lint "$out" >/dev/null 2>&1 || { echo "ERROR: $out is not a valid property list (plutil -lint failed)" >&2; exit 1; }
}
TMP_PLISTS="$(mktemp -d)"
gen_plist "$PLIST_SRC/$LABEL.plist"              "$TMP_PLISTS/$LABEL.plist"
gen_plist "$PLIST_SRC/$UPDATE_LABEL.plist"       "$TMP_PLISTS/$UPDATE_LABEL.plist"
install -m 644 "$TMP_PLISTS/$LABEL.plist"        "$DAEMON_DIR/$LABEL.plist"
install -m 644 "$TMP_PLISTS/$UPDATE_LABEL.plist" "$DAEMON_DIR/$UPDATE_LABEL.plist"
rm -rf "$TMP_PLISTS"
install -m 755 "$PLIST_SRC/dispatch-update.sh"   "$INSTALL_DIR/dispatch-update.sh"
# Keep a copy of this script outside the release symlink, so the machine carries its own uninstaller and
# re-configurer. Before this, a command-line install left
# nothing: after the download folder was deleted there was no command to run and no way to find out the
# service was even installed (it has no /Applications entry - it is a LaunchDaemon, not an app).
install -m 755 "$SCRIPT_DIR/install.sh" "$INSTALL_DIR/install.sh"
# Release public key for the updater's independent signature re-verify (defense-in-depth over the app check).
PUBKEY_SRC="$PLIST_SRC/dispatch-update-public.pem"
[[ -f "$PUBKEY_SRC" ]] || PUBKEY_SRC="$SOURCE_DIR/src/Dispatch.Core/Updates/dispatch-update-public.pem"
install -m 644 "$PUBKEY_SRC" "$INSTALL_DIR/dispatch-update-public.pem"
# Mark this install self-managed so the dashboard exposes the "upload upgrade package" flow.
mkdir -p "$DATA_DIR/updates"; touch "$DATA_DIR/updates/.self-managed"
chown -R "$OWNER:$OWNER_GROUP" "$DATA_DIR/updates"

# Load them. `bootstrap` is the modern verb (it replaces the older `load -w`, and unlike `load` it re-reads
# an already-loaded job only after a bootout - which we did above). If neither is available, the files are in
# /Library/LaunchDaemons and launchd picks them up at the next boot regardless.
if [[ "$NO_START" == "1" ]]; then
  echo "==> Service staged (daemons installed; launchd starts them at the next boot, or run:"
  echo "      sudo launchctl bootstrap system $DAEMON_DIR/$LABEL.plist)"
  exit 0
fi
# Clear any persisted "disabled" override before loading. `launchctl disable` writes an override keyed by the
# service LABEL into launchd's own database, and it survives deleting the plist and reinstalling the product.
# Since this script's own --uninstall calls disable, a machine that was ever uninstalled and then reinstalled
# would bootstrap with exit status 0, print a success banner, and never start anything - launchd honors the
# stale override over RunAtLoad and KeepAlive alike. enable + bootstrap is the documented pairing.
for _l in "$LABEL" "$UPDATE_LABEL"; do
  launchctl enable "system/${_l}" >/dev/null 2>&1 || true
done

if ! launchctl bootstrap system "$DAEMON_DIR/$LABEL.plist" 2>/dev/null; then
  launchctl load -w "$DAEMON_DIR/$LABEL.plist" 2>/dev/null \
    || { echo "ERROR: launchd refused to load $DAEMON_DIR/$LABEL.plist (plutil -lint says: $(plutil -lint "$DAEMON_DIR/$LABEL.plist" 2>&1))" >&2; exit 1; }
fi
launchctl bootstrap system "$DAEMON_DIR/$UPDATE_LABEL.plist" 2>/dev/null \
  || launchctl load -w "$DAEMON_DIR/$UPDATE_LABEL.plist" 2>/dev/null || true

# Open firewall ports if the Application Firewall is enabled (best-effort). The daemon binds the listening
# sockets, so this only matters for the "block all incoming connections" setting.
if [[ "$NO_FIREWALL" != "1" ]] && [[ -x /usr/libexec/ApplicationFirewall/socketfilterfw ]]; then
  if /usr/libexec/ApplicationFirewall/socketfilterfw --getglobalstate 2>/dev/null | grep -qi "enabled"; then
    # Current macOS has no per-port firewall API (the Application Firewall is per-binary and otherwise
    # stealth: an inbound connect to a listening daemon is allowed unless "block all" is set). So the only
    # useful rule is naming the binary and making sure it is not blocked.
    /usr/libexec/ApplicationFirewall/socketfilterfw --add "$REL_DIR/Dispatch.Service" >/dev/null 2>&1 || true
    /usr/libexec/ApplicationFirewall/socketfilterfw --unblockappblock "$REL_DIR/Dispatch.Service" >/dev/null 2>&1 || true
  fi
fi

# Prove it came up. Everything above can succeed while nothing is actually running - a stale launchd
# override, a signature the kernel refuses to exec, a dashboard port already held by something else. Until
# now this script asserted success regardless, which on the app path (where install.sh's stdout goes to a
# log file nobody opens) leaves the operator with "I installed it and nothing happened" and no lead. So poll
# /health, and if it never answers, print what launchd and the daemon say and exit non-zero - that turns a
# silently dead install into a FAILED one in Installer.app, which is the honest outcome.
echo "==> Waiting for the dashboard on https://localhost:$HTTP_PORT"
up=""
for _i in $(seq 1 30); do
  if curl -fsSk -m 3 "https://localhost:$HTTP_PORT/health" >/dev/null 2>&1; then up=1; break; fi
  sleep 2
done
if [[ -z "$up" ]]; then
  echo "ERROR: Dispatch is not answering https://localhost:$HTTP_PORT after 60s." >&2
  echo "       launchd's view of the job:" >&2
  launchctl print "system/$LABEL" 2>&1 \
    | { grep -E "^[[:space:]]*(state|pid|last exit code|exit reason|runs|run type|program)" || echo "         (launchd has no such job loaded)"; } \
    | sed 's/^/         /' >&2
  echo "       last lines of $LOG_DIR/launchd.err.log:" >&2
  { tail -5 "$LOG_DIR/launchd.err.log" 2>/dev/null | sed 's/^/         /' >&2 \
      || echo "         (no such file - which usually means launchd never exec'd the binary at all)"; } >&2
  echo "       last lines of $LOG_DIR/dispatch.jsonl:" >&2
  { tail -3 "$LOG_DIR/dispatch.jsonl" 2>/dev/null | cut -c1-400 | sed 's/^/         /' >&2 \
    || echo "         (no application log yet)"; } >&2
  echo "       Port in use by something else? sudo lsof -nP -iTCP:$HTTP_PORT -sTCP:LISTEN" >&2
  echo "       Everything install.sh printed: cat /tmp/dispatch-pkg-postinstall.log (pkg) or the terminal above." >&2
  exit 1
fi

echo
echo "Dispatch is installed and running."
echo "  Dashboard: https://localhost:$HTTP_PORT  (self-signed cert; log in with the admin password you set)"
echo "  SMTP:      25 and 587, falling back to 2525 when 25 cannot be bound (in use, or unprivileged)"
echo "  Ports, retention and other settings are managed in the dashboard."
echo "  Status:    sudo $INSTALL_DIR/install.sh --status"
echo "  Stop/Start: sudo $INSTALL_DIR/install.sh --stop | --start"
echo "  Uninstall: sudo $INSTALL_DIR/install.sh --uninstall"
echo "  Logs:      $LOG_DIR  (and: log show --last 10m --predicate 'process == \"Dispatch.Service\"')"
if [[ -z "$SERVICE_USER" ]]; then
  echo "  The daemon runs as root so it can bind SMTP 25/587 (macOS has no CAP_NET_BIND_SERVICE)."
  echo "  Prefer an unprivileged service? Re-run with --user <name>; SMTP then listens on 2525."
fi
