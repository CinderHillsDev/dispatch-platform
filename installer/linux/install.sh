#!/usr/bin/env bash
#
# Dispatch SMTP Relay — Linux installer.
#
# Publishes the service, lays out config/data directories, and installs a systemd unit.
# The SQL connection string and admin password are supplied here at install time (the admin
# password is required — the web UI is not accessible without it; if omitted you'll be prompted).
#
# Usage:
#   sudo ./install.sh \
#       --sql-connection "Server=localhost,1433;Database=DispatchLog;User Id=sa;Password=...;TrustServerCertificate=True;Encrypt=True" \
#       --admin-password "<dashboard admin password>" \
#       [--http-port 8420] [--api-port 8421] [--smtp-ports 25,587] [--source <repo path>]
#
set -euo pipefail

INSTALL_DIR="/opt/dispatch"
CONFIG_DIR="/etc/dispatch"
DATA_DIR="/var/lib/dispatch"
LOG_DIR="/var/log/dispatch"
HTTP_PORT="8420"
API_PORT="8421"
SMTP_PORTS="25,587"
SQL_CONNECTION=""
ADMIN_PASSWORD=""
SOURCE_DIR=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --sql-connection) SQL_CONNECTION="$2"; shift 2;;
    --admin-password) ADMIN_PASSWORD="$2"; shift 2;;
    --http-port) HTTP_PORT="$2"; shift 2;;
    --api-port) API_PORT="$2"; shift 2;;
    --smtp-ports) SMTP_PORTS="$2"; shift 2;;
    --source) SOURCE_DIR="$2"; shift 2;;
    *) echo "Unknown option: $1" >&2; exit 1;;
  esac
done

[[ $EUID -eq 0 ]] || { echo "Please run as root (sudo)." >&2; exit 1; }
[[ -n "$SQL_CONNECTION" ]] || { echo "--sql-connection is required." >&2; exit 1; }

if [[ -z "$ADMIN_PASSWORD" ]]; then
  read -rsp "Set the dashboard admin password: " ADMIN_PASSWORD; echo
  [[ -n "$ADMIN_PASSWORD" ]] || { echo "Admin password is required." >&2; exit 1; }
fi

# Resolve repo source (default: two levels up from this script).
SOURCE_DIR="${SOURCE_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

echo "==> Building the web UI"
( cd "$SOURCE_DIR/src/Dispatch.UI" && npm ci && npm run build )
rm -rf "$SOURCE_DIR/src/Dispatch.Web/wwwroot"
mkdir -p "$SOURCE_DIR/src/Dispatch.Web/wwwroot"
cp -r "$SOURCE_DIR/src/Dispatch.UI/dist/." "$SOURCE_DIR/src/Dispatch.Web/wwwroot/"

echo "==> Publishing the service to $INSTALL_DIR"
mkdir -p "$INSTALL_DIR"
dotnet publish "$SOURCE_DIR/src/Dispatch.Service" -c Release -o "$INSTALL_DIR"

echo "==> Creating the 'dispatch' service account and directories"
id -u dispatch >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin dispatch
mkdir -p "$CONFIG_DIR" "$DATA_DIR/spool" "$LOG_DIR"

echo "==> Writing $CONFIG_DIR/appsettings.json"
cat > "$CONFIG_DIR/appsettings.json" <<JSON
{
  "ConnectionStrings": { "DispatchLog": "${SQL_CONNECTION//\"/\\\"}" },
  "AdminPassword": "${ADMIN_PASSWORD//\"/\\\"}",
  "Spool": { "Directory": "$DATA_DIR/spool", "WorkerCount": 4 },
  "Listener": { "Ports": [ ${SMTP_PORTS} ], "AllowedCidrs": [ "127.0.0.1/32", "::1/128" ] },
  "Api": { "Port": $API_PORT },
  "WebUi": { "Port": $HTTP_PORT }
}
JSON

# The admin password is consumed once on first start (hashed into SQL); leaving it here is acceptable
# because the file is root/dispatch-only, but you may remove the AdminPassword line afterwards.
chown -R dispatch:dispatch "$INSTALL_DIR" "$DATA_DIR" "$LOG_DIR" "$CONFIG_DIR"
chmod 600 "$CONFIG_DIR/appsettings.json"

# Spool file security (spec §14.5). The spool directory and its incoming/processing/failed
# subdirectories are owned by dispatch:dispatch with mode 700. The .eml/.meta files written into
# them by the service contain raw message content, so they must be created with mode 600 — the
# service is started with `UMask=0177` (see dispatch.service) so every spool file inherits
# rw------- (600). We pre-create the subdirectories here so their mode is correct from first start.
mkdir -p "$DATA_DIR/spool/incoming" "$DATA_DIR/spool/processing" "$DATA_DIR/spool/failed"
chown -R dispatch:dispatch "$DATA_DIR/spool"
chmod 700 "$DATA_DIR/spool" "$DATA_DIR/spool/incoming" "$DATA_DIR/spool/processing" "$DATA_DIR/spool/failed"

echo "==> Installing systemd unit"
install -m 644 "$SOURCE_DIR/installer/linux/dispatch.service" /etc/systemd/system/dispatch.service
systemctl daemon-reload
systemctl enable --now dispatch

echo
echo "Dispatch is installed and running."
echo "  Dashboard: http://localhost:$HTTP_PORT  (log in with the admin password you set)"
echo "  Status:    systemctl status dispatch"
echo "  Logs:      journalctl -u dispatch -f   (and $LOG_DIR)"
