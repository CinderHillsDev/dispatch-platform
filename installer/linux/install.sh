#!/usr/bin/env bash
#
# Dispatch SMTP Relay — Linux installer.
#
# Publishes the service, lays out config/data directories, and installs a systemd unit. Per spec §12.1 the
# only things written to appsettings.json are the SQL connection string, the install-time admin-password
# seed, and (optionally) the Web UI TLS cert — everything else lives in the SQL config table and is managed
# from the dashboard after first run (default ports: SMTP 2525, dashboard 8420, API 8025).
#
# Usage:
#   # Use an existing SQL Server / Azure SQL:
#   sudo ./install.sh --sql-connection "Server=...;Database=DispatchLog;User Id=...;Password=...;TrustServerCertificate=True;Encrypt=True" --admin-password "<pw>"
#
#   # Or have the installer set up SQL Server Express locally (Ubuntu/Debian or RHEL/Fedora):
#   sudo ./install.sh --install-sql --sa-password "<StrongSaPassw0rd!>" --admin-password "<pw>" [--generate-cert]
#
#   # From a release tarball (self-contained binaries; no .NET SDK / Node required on the box):
#   sudo ./install.sh --prebuilt ./bin --install-sql --sa-password "<pw>" --admin-password "<pw>"
#
# Flags:
#   --sql-connection <s>   Connection string for an existing server (omit when using --install-sql).
#   --install-sql          Install SQL Server (Express, free) locally + create the DispatchLog DB. On arm64
#                          (no SQL Server build) this runs Azure SQL Edge in a container instead — needs Docker.
#   --sa-password <s>      SA password for --install-sql (required with --install-sql; must meet SQL policy).
#   --admin-password <s>   Dashboard admin password seed (prompted if omitted).
#   --generate-cert        Generate a self-signed PFX and serve the dashboard over HTTPS (spec §17.2).
#   --http-port <n>        Firewall/URL dashboard port (default 8420; change in the dashboard to differ).
#   --api-port <n>         Firewall ingestion API port (default 8025).
#   --smtp-ports <a,b>     Firewall SMTP ports (default 2525; set 25,587 in the dashboard for production).
#   --source <path>        Repo source root (default: two levels up from this script). Build-from-source mode.
#   --prebuilt <dir>       Install pre-published self-contained binaries from <dir> instead of building from
#                          source. Used by the release tarball; needs neither the .NET SDK nor Node.
#   --no-start             Stage the install but don't start the service (the unit is enabled, not started).
#                          Used when baking the appliance image; first boot configures SQL and starts it.
#
set -euo pipefail

INSTALL_DIR="/opt/dispatch"
DATA_DIR="/var/lib/dispatch"
# Config + spool live together under the data dir (the service's content root), mirroring the Windows
# ProgramData layout. The spool default (./.dispatch-spool, from the SQL config) resolves under here.
CONFIG_DIR="$DATA_DIR"
LOG_DIR="/var/log/dispatch"
HTTP_PORT="8420"
API_PORT="8025"
SMTP_PORTS="2525"
SQL_CONNECTION=""
ADMIN_PASSWORD=""
SOURCE_DIR=""
PREBUILT_DIR=""
INSTALL_SQL="0"
SA_PASSWORD=""
GENERATE_CERT="0"
TLS_CERT_PATH=""
TLS_CERT_PASSWORD=""
NO_START="0"

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
    --prebuilt) PREBUILT_DIR="$2"; shift 2;;
    # Stage the install without starting the service (the unit is enabled, not started). Used when building
    # the prebuilt appliance image, where SQL is configured and the service is started on first boot.
    --no-start) NO_START="1"; shift;;
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

  # SQL Server has no arm64 Linux build, so on arm64 fall back to Azure SQL Edge, which is arm64-native
  # but ships only as a container image. This is a dev/test convenience — SQL Edge is deprecated by
  # Microsoft; for production use an amd64 host with SQL Server, or point --sql-connection at an external
  # instance.
  local arch; arch="$(uname -m)"
  if [[ "$arch" == "aarch64" || "$arch" == "arm64" ]]; then
    install_sql_edge_container
    return
  fi

  echo "==> Installing SQL Server (Express) for $ID $VERSION_ID ($arch)"
  if command -v apt-get >/dev/null 2>&1; then
    # Prerequisites the rest of this function needs (a minimal cloud/VM image often lacks gnupg + the
    # https apt transport); install them before using gpg so the key import can't fail with "gpg: not found".
    apt-get update -y || true
    apt-get install -y curl ca-certificates gnupg apt-transport-https
    # SQL Server 2025 (supports Ubuntu 24.04). The 2025 .list uses signed-by=/usr/share/keyrings/microsoft-prod.gpg,
    # so install the Microsoft key there.
    install -d -m 0755 /usr/share/keyrings
    curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --batch --yes --dearmor -o /usr/share/keyrings/microsoft-prod.gpg
    chmod a+r /usr/share/keyrings/microsoft-prod.gpg
    # Use the repo list for this distro release; fall back to the newest known LTS list if MS has no list
    # for this exact ${VERSION_ID} yet (e.g. a brand-new Ubuntu before the SQL repo catches up).
    curl -fsSL "https://packages.microsoft.com/config/${ID}/${VERSION_ID}/mssql-server-2025.list" -o /etc/apt/sources.list.d/mssql-server-2025.list || \
      curl -fsSL "https://packages.microsoft.com/config/ubuntu/24.04/mssql-server-2025.list" -o /etc/apt/sources.list.d/mssql-server-2025.list
    apt-get update -y
    ACCEPT_EULA=Y MSSQL_PID=Express MSSQL_SA_PASSWORD="$SA_PASSWORD" apt-get install -y mssql-server
    apt-get install -y mssql-tools18 unixodbc-dev || true
  elif command -v dnf >/dev/null 2>&1 || command -v yum >/dev/null 2>&1; then
    local PM; PM="$(command -v dnf || command -v yum)"
    curl -fsSL "https://packages.microsoft.com/config/rhel/${VERSION_ID%%.*}/mssql-server-2025.repo" -o /etc/yum.repos.d/mssql-server-2025.repo
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

# ---- arm64 fallback: Azure SQL Edge in a container ---------------------------------------------
# SQL Server is amd64-only on Linux; Azure SQL Edge speaks the same wire protocol and runs natively on
# arm64. Needs a container runtime (docker/podman); the DispatchLog database is created by the service's
# own DatabaseInitializer on first start, so no sqlcmd is required.
install_sql_edge_container() {
  echo "==> arm64 detected — SQL Server has no arm64 Linux build; using Azure SQL Edge (container) instead."
  local runtime=""
  if command -v docker >/dev/null 2>&1; then runtime="docker"
  elif command -v podman >/dev/null 2>&1; then runtime="podman"
  elif command -v apt-get >/dev/null 2>&1; then
    echo "==> Installing a container runtime (docker.io)"
    apt-get update -y || true
    if apt-get install -y docker.io; then
      systemctl enable --now docker || true
      runtime="docker"
    fi
  fi
  [[ -n "$runtime" ]] || {
    echo "No container runtime found and Docker could not be installed automatically." >&2
    echo "Install Docker or Podman and re-run, or pass --sql-connection to an external SQL instance." >&2
    exit 1
  }

  echo "==> Starting Azure SQL Edge via $runtime (instance 'dispatch-sql' on port 1433)"
  "$runtime" rm -f dispatch-sql >/dev/null 2>&1 || true
  "$runtime" run -d --name dispatch-sql \
    -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=${SA_PASSWORD}" \
    -p 1433:1433 -v dispatch-sql-data:/var/opt/mssql \
    --restart unless-stopped \
    mcr.microsoft.com/azure-sql-edge:latest

  echo "==> Waiting for Azure SQL Edge to accept TCP on 1433"
  for _ in $(seq 1 36); do
    if (exec 3<>/dev/tcp/127.0.0.1/1433) 2>/dev/null; then exec 3>&- 3<&-; break; fi
    sleep 5
  done
  # The service's DatabaseInitializer connects to master and creates DispatchLog on first start.
  SQL_CONNECTION="Server=localhost,1433;Database=DispatchLog;User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=True;Encrypt=True"
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

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Auto-select prebuilt binaries by CPU arch when --prebuilt wasn't given, so one tarball + one command
# works on any Linux. Prefer a per-arch dir (bin-x64 / bin-arm64) shipped in a universal tarball, then a
# plain bin/ (single-arch tarball). If none are found we fall through to build-from-source (needs the SDK).
if [[ -z "$PREBUILT_DIR" ]]; then
  case "$(uname -m)" in
    x86_64|amd64)  _arch=x64 ;;
    aarch64|arm64) _arch=arm64 ;;
    *)             _arch="" ;;
  esac
  if [[ -n "$_arch" && -d "$SCRIPT_DIR/bin-$_arch" ]]; then PREBUILT_DIR="$SCRIPT_DIR/bin-$_arch"
  elif [[ -d "$SCRIPT_DIR/bin" ]]; then PREBUILT_DIR="$SCRIPT_DIR/bin"
  fi
  [[ -n "$PREBUILT_DIR" ]] && echo "==> Using prebuilt binaries for $(uname -m): $PREBUILT_DIR"
fi

# Stop any previously-installed service first, otherwise its running binary is "Text file busy" and the
# upgrade copy fails. Best-effort: ignored on a fresh install where the unit doesn't exist yet.
systemctl stop dispatch 2>/dev/null || true

mkdir -p "$INSTALL_DIR"
if [[ -n "$PREBUILT_DIR" ]]; then
  # Release-tarball mode: copy the self-contained publish output as-is (no SDK / Node build).
  # A relative --prebuilt is resolved against the current dir, then against the script's own dir, so
  # `--prebuilt ./bin` works whether you run it from inside the extracted folder or by path.
  if [[ ! -d "$PREBUILT_DIR" && -d "$SCRIPT_DIR/$PREBUILT_DIR" ]]; then PREBUILT_DIR="$SCRIPT_DIR/$PREBUILT_DIR"; fi
  [[ -d "$PREBUILT_DIR" ]] || { echo "--prebuilt dir not found: $PREBUILT_DIR (cwd: $PWD, script: $SCRIPT_DIR)" >&2; exit 1; }
  [[ -f "$PREBUILT_DIR/Dispatch.Service" ]] || { echo "--prebuilt dir has no Dispatch.Service executable: $PREBUILT_DIR" >&2; exit 1; }
  # A self-contained .NET build still needs the system ICU library for globalization (it aborts at
  # startup without it) plus TLS roots — minimal images often lack both.
  echo "==> Ensuring .NET runtime dependencies (ICU, CA certs)"
  if command -v apt-get >/dev/null 2>&1; then
    apt-get update -y >/dev/null 2>&1 || true
    apt-get install -y ca-certificates || true
    # On Debian/Ubuntu the ICU runtime is version-numbered (libicu74, libicu76, …) — there is no plain
    # "libicu" package. Resolve the highest-versioned one; fall back to libicu-dev which always pulls it.
    icu_pkg="$(apt-cache --names-only search '^libicu[0-9]+$' 2>/dev/null | awk '{print $1}' | sort -V | tail -1)"
    apt-get install -y "${icu_pkg:-libicu-dev}" || echo "WARN: could not install the ICU runtime — install libicu manually if the service fails to start." >&2
  elif command -v dnf >/dev/null 2>&1; then dnf install -y libicu ca-certificates || true
  elif command -v yum >/dev/null 2>&1; then yum install -y libicu ca-certificates || true
  fi
  echo "==> Installing pre-built binaries from $PREBUILT_DIR to $INSTALL_DIR"
  cp -r "$PREBUILT_DIR/." "$INSTALL_DIR/"
  chmod +x "$INSTALL_DIR/Dispatch.Service"
else
  # Build-from-source mode: needs the .NET SDK + Node.
  SOURCE_DIR="${SOURCE_DIR:-$(cd "$SCRIPT_DIR/../.." && pwd)}"
  echo "==> Building the web UI"
  ( cd "$SOURCE_DIR/src/Dispatch.UI" && npm ci && npm run build )
  rm -rf "$SOURCE_DIR/src/Dispatch.Web/wwwroot"
  mkdir -p "$SOURCE_DIR/src/Dispatch.Web/wwwroot"
  cp -r "$SOURCE_DIR/src/Dispatch.UI/dist/." "$SOURCE_DIR/src/Dispatch.Web/wwwroot/"

  echo "==> Publishing the service to $INSTALL_DIR"
  dotnet publish "$SOURCE_DIR/src/Dispatch.Service" -c Release -o "$INSTALL_DIR"
fi

echo "==> Creating the 'dispatch' service account and directories"
id -u dispatch >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin dispatch
mkdir -p "$CONFIG_DIR" "$DATA_DIR/.dispatch-spool" "$LOG_DIR"

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

# The admin password is consumed once on first start: hashed into SQL, then the plaintext seed is wiped
# from appsettings.json by the service so the password lives only in the database. File is root/dispatch-only.
chown -R dispatch:dispatch "$INSTALL_DIR" "$DATA_DIR" "$LOG_DIR" "$CONFIG_DIR"
chmod 600 "$CONFIG_DIR/appsettings.json"
[[ -n "$TLS_CERT_PATH" ]] && chown dispatch:dispatch "$TLS_CERT_PATH"

# Spool file security (spec §14.5): pre-create the subdirectories mode 700 so they're correct from first
# start; the service runs with UMask=0177 so every .eml/.meta is created rw------- (600).
mkdir -p "$DATA_DIR/.dispatch-spool/incoming" "$DATA_DIR/.dispatch-spool/processing" "$DATA_DIR/.dispatch-spool/failed"
chown -R dispatch:dispatch "$DATA_DIR/.dispatch-spool"
chmod 700 "$DATA_DIR/.dispatch-spool" "$DATA_DIR/.dispatch-spool/incoming" "$DATA_DIR/.dispatch-spool/processing" "$DATA_DIR/.dispatch-spool/failed"

echo "==> Installing systemd unit"
# The unit lives next to this script in a release tarball, or under installer/linux in a source tree.
if [[ -f "$SCRIPT_DIR/dispatch.service" ]]; then
  UNIT_SRC="$SCRIPT_DIR/dispatch.service"
else
  UNIT_SRC="$SOURCE_DIR/installer/linux/dispatch.service"
fi
install -m 644 "$UNIT_SRC" /etc/systemd/system/dispatch.service
systemctl daemon-reload
if [[ "$NO_START" == "1" ]]; then
  systemctl enable dispatch                      # appliance build: started on first boot, not now
  echo "==> Service enabled (not started — --no-start)"
else
  systemctl enable --now dispatch
fi

# Open firewall ports if a supported firewall is active (best-effort).
if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
  for p in "$HTTP_PORT" "$API_PORT" ${SMTP_PORTS//,/ }; do ufw allow "$p"/tcp >/dev/null 2>&1 || true; done
elif command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
  for p in "$HTTP_PORT" "$API_PORT" ${SMTP_PORTS//,/ }; do firewall-cmd --permanent --add-port="$p"/tcp >/dev/null 2>&1 || true; done
  firewall-cmd --reload >/dev/null 2>&1 || true
fi

# The dashboard is always HTTPS (a self-signed cert is generated when no TLS cert is configured).
SCHEME="https"
if [[ "$NO_START" == "1" ]]; then
  echo
  echo "Dispatch is staged (service enabled, not started) — it will start on first boot."
  exit 0
fi
echo
echo "Dispatch is installed and running."
echo "  Dashboard: ${SCHEME}://localhost:$HTTP_PORT  (log in with the admin password you set)"
echo "  Ports, retention and other settings are managed in the dashboard (default SMTP port 2525)."
echo "  Status:    systemctl status dispatch"
echo "  Logs:      journalctl -u dispatch -f   (and $LOG_DIR)"
