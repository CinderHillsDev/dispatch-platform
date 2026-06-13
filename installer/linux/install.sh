#!/usr/bin/env bash
#
# Dispatch SMTP Relay — Linux installer.
#
# Publishes the service, lays out config/data directories, and installs a systemd unit. Per spec §12.1 the
# only things written to appsettings.json are the SQL connection string, the install-time admin-password
# seed, and (optionally) the Web UI TLS cert — everything else lives in the SQL config table and is managed
# from the dashboard after first run (default ports: SMTP 2525, dashboard 8420, API 8421).
#
# Usage:
#   # Use an existing SQL Server / Azure SQL:
#   sudo ./install.sh --sql-connection "Server=...;Database=DispatchLog;User Id=...;Password=...;TrustServerCertificate=True;Encrypt=True" --admin-password "<pw>"
#
#   # Or have the installer set up SQL Server Express locally (Ubuntu/Debian or RHEL/Fedora):
#   sudo ./install.sh --install-sql --sa-password "<StrongSaPassw0rd!>" --admin-password "<pw>" [--generate-cert]
#
# Flags:
#   --sql-connection <s>   Connection string for an existing server (omit when using --install-sql).
#   --install-sql          Install SQL Server (Express edition, free) locally, create the DispatchLog DB.
#   --sa-password <s>      SA password for --install-sql (required with --install-sql; must meet SQL policy).
#   --admin-password <s>   Dashboard admin password seed (prompted if omitted).
#   --generate-cert        Generate a self-signed PFX and serve the dashboard over HTTPS (spec §17.2).
#   --http-port <n>        Firewall/URL dashboard port (default 8420; change in the dashboard to differ).
#   --api-port <n>         Firewall ingestion API port (default 8421).
#   --smtp-ports <a,b>     Firewall SMTP ports (default 2525; set 25,587 in the dashboard for production).
#   --source <path>        Repo source root (default: two levels up from this script).
#
set -euo pipefail

INSTALL_DIR="/opt/dispatch"
CONFIG_DIR="/etc/dispatch"
DATA_DIR="/var/lib/dispatch"
LOG_DIR="/var/log/dispatch"
HTTP_PORT="8420"
API_PORT="8421"
SMTP_PORTS="2525"
SQL_CONNECTION=""
ADMIN_PASSWORD=""
SOURCE_DIR=""
INSTALL_SQL="0"
SA_PASSWORD=""
GENERATE_CERT="0"
TLS_CERT_PATH=""
TLS_CERT_PASSWORD=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --sql-connection) SQL_CONNECTION="$2"; shift 2;;
    --admin-password) ADMIN_PASSWORD="$2"; shift 2;;
    --install-sql) INSTALL_SQL="1"; shift;;
    --sa-password) SA_PASSWORD="$2"; shift 2;;
    --generate-cert) GENERATE_CERT="1"; shift;;
    --http-port) HTTP_PORT="$2"; shift 2;;
    --api-port) API_PORT="$2"; shift 2;;
    --smtp-ports) SMTP_PORTS="$2"; shift 2;;
    --source) SOURCE_DIR="$2"; shift 2;;
    *) echo "Unknown option: $1" >&2; exit 1;;
  esac
done

[[ $EUID -eq 0 ]] || { echo "Please run as root (sudo)." >&2; exit 1; }

# ---- Optional: install SQL Server Express locally -----------------------------------------------
# Installs Microsoft's mssql-server (Express edition = free, set via MSSQL_PID), runs the unattended
# setup, and creates the DispatchLog database. Best-effort across Debian/Ubuntu (apt) and RHEL/Fedora
# (dnf/yum); validate on your target distro. Sets SQL_CONNECTION to the local SA connection.
install_sql_server() {
  [[ -n "$SA_PASSWORD" ]] || { echo "--sa-password is required with --install-sql." >&2; exit 1; }
  . /etc/os-release
  echo "==> Installing SQL Server (Express) for $ID $VERSION_ID"
  if command -v apt-get >/dev/null 2>&1; then
    # The mssql-server .list has no signed-by=, so apt uses the global trusted keyrings — install the
    # Microsoft key into /etc/apt/trusted.gpg.d/ (not /usr/share/keyrings, which apt would ignore here).
    curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --batch --yes --dearmor -o /etc/apt/trusted.gpg.d/microsoft.gpg
    chmod a+r /etc/apt/trusted.gpg.d/microsoft.gpg
    curl -fsSL "https://packages.microsoft.com/config/${ID}/${VERSION_ID}/mssql-server-2022.list" -o /etc/apt/sources.list.d/mssql-server-2022.list || \
      curl -fsSL "https://packages.microsoft.com/config/ubuntu/22.04/mssql-server-2022.list" -o /etc/apt/sources.list.d/mssql-server-2022.list
    apt-get update -y
    ACCEPT_EULA=Y MSSQL_PID=Express MSSQL_SA_PASSWORD="$SA_PASSWORD" apt-get install -y mssql-server
    apt-get install -y mssql-tools18 unixodbc-dev || true
  elif command -v dnf >/dev/null 2>&1 || command -v yum >/dev/null 2>&1; then
    local PM; PM="$(command -v dnf || command -v yum)"
    curl -fsSL "https://packages.microsoft.com/config/rhel/${VERSION_ID%%.*}/mssql-server-2022.repo" -o /etc/yum.repos.d/mssql-server-2022.repo
    ACCEPT_EULA=Y MSSQL_PID=Express MSSQL_SA_PASSWORD="$SA_PASSWORD" "$PM" install -y mssql-server
    "$PM" install -y mssql-tools18 unixODBC-devel || true
  else
    echo "Unsupported package manager — install SQL Server manually and pass --sql-connection." >&2; exit 1
  fi

  echo "==> Running unattended SQL Server setup (Express edition)"
  MSSQL_PID=Express MSSQL_SA_PASSWORD="$SA_PASSWORD" ACCEPT_EULA=Y /opt/mssql/bin/mssql-conf -n setup accept-eula
  systemctl enable --now mssql-server

  echo "==> Waiting for SQL Server and creating the DispatchLog database"
  local sqlcmd; sqlcmd="$(command -v sqlcmd || echo /opt/mssql-tools18/bin/sqlcmd)"
  for _ in $(seq 1 30); do
    if "$sqlcmd" -S localhost -U sa -P "$SA_PASSWORD" -C -Q "SELECT 1" >/dev/null 2>&1; then break; fi
    sleep 5
  done
  "$sqlcmd" -S localhost -U sa -P "$SA_PASSWORD" -C -Q "IF DB_ID('DispatchLog') IS NULL CREATE DATABASE [DispatchLog];"
  SQL_CONNECTION="Server=localhost;Database=DispatchLog;User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=True;Encrypt=True"
}

# ---- Optional: generate a self-signed TLS cert for the dashboard --------------------------------
generate_cert() {
  command -v openssl >/dev/null 2>&1 || { echo "openssl is required for --generate-cert." >&2; exit 1; }
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

[[ "$INSTALL_SQL" == "1" ]] && install_sql_server
[[ -n "$SQL_CONNECTION" ]] || { echo "Provide --sql-connection, or use --install-sql." >&2; exit 1; }

if [[ -z "$ADMIN_PASSWORD" ]]; then
  read -rsp "Set the dashboard admin password: " ADMIN_PASSWORD; echo
  [[ -n "$ADMIN_PASSWORD" ]] || { echo "Admin password is required." >&2; exit 1; }
fi

[[ "$GENERATE_CERT" == "1" ]] && generate_cert

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
# Spec §12.1: appsettings holds ONLY the connection string, the admin-password seed, and the Web UI TLS
# cert. Ports/spool/retry/etc. are seeded into the SQL config table on first run and managed in the dashboard.
WEBUI_TLS=""
if [[ -n "$TLS_CERT_PATH" ]]; then
  WEBUI_TLS=",
  \"WebUi\": { \"TlsCertPath\": \"${TLS_CERT_PATH//\"/\\\"}\", \"TlsCertPassword\": \"${TLS_CERT_PASSWORD//\"/\\\"}\" }"
fi
cat > "$CONFIG_DIR/appsettings.json" <<JSON
{
  "ConnectionStrings": { "DispatchLog": "${SQL_CONNECTION//\"/\\\"}" },
  "AdminPassword": "${ADMIN_PASSWORD//\"/\\\"}",
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } }${WEBUI_TLS}
}
JSON

# The admin password is consumed once on first start (hashed into SQL); the file is root/dispatch-only.
chown -R dispatch:dispatch "$INSTALL_DIR" "$DATA_DIR" "$LOG_DIR" "$CONFIG_DIR"
chmod 600 "$CONFIG_DIR/appsettings.json"
[[ -n "$TLS_CERT_PATH" ]] && chown dispatch:dispatch "$TLS_CERT_PATH"

# Spool file security (spec §14.5): pre-create the subdirectories mode 700 so they're correct from first
# start; the service runs with UMask=0177 so every .eml/.meta is created rw------- (600).
mkdir -p "$DATA_DIR/spool/incoming" "$DATA_DIR/spool/processing" "$DATA_DIR/spool/failed"
chown -R dispatch:dispatch "$DATA_DIR/spool"
chmod 700 "$DATA_DIR/spool" "$DATA_DIR/spool/incoming" "$DATA_DIR/spool/processing" "$DATA_DIR/spool/failed"

echo "==> Installing systemd unit"
install -m 644 "$SOURCE_DIR/installer/linux/dispatch.service" /etc/systemd/system/dispatch.service
systemctl daemon-reload
systemctl enable --now dispatch

# Open firewall ports if a supported firewall is active (best-effort).
if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
  for p in "$HTTP_PORT" "$API_PORT" ${SMTP_PORTS//,/ }; do ufw allow "$p"/tcp >/dev/null 2>&1 || true; done
elif command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
  for p in "$HTTP_PORT" "$API_PORT" ${SMTP_PORTS//,/ }; do firewall-cmd --permanent --add-port="$p"/tcp >/dev/null 2>&1 || true; done
  firewall-cmd --reload >/dev/null 2>&1 || true
fi

SCHEME="http"; [[ -n "$TLS_CERT_PATH" ]] && SCHEME="https"
echo
echo "Dispatch is installed and running."
echo "  Dashboard: ${SCHEME}://localhost:$HTTP_PORT  (log in with the admin password you set)"
echo "  Ports, retention and other settings are managed in the dashboard (default SMTP port 2525)."
echo "  Status:    systemctl status dispatch"
echo "  Logs:      journalctl -u dispatch -f   (and $LOG_DIR)"
