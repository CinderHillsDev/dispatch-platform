# Installers

Per spec §12.1, the only things written to the platform config file are the **SQL connection string**, the
install-time **admin-password seed**, and (optionally) the **Web UI TLS cert**. Everything else (ports,
spool, retry, retention, …) is seeded into the SQL `config` table on first run and managed from the
dashboard. On first start the admin-password seed is hashed (bcrypt) into the `config` table; if you omit
it, the dashboard shows a one-time first-run setup screen instead.

## Windows (`windows/`)

Three ways to install, from most to least automated:

### 1. Bundle (`windows/bundle/Bundle.wxs`) — recommended

A WiX **Burn bundle** (`DispatchSetup.exe`) that chains, in order:

1. **SQL Server Express** — `sql-express/InstallSqlExpress.exe` runs `InstallSqlExpress.ps1`, which downloads
   and silently installs SQL Server Express as the named instance **`DISPATCHSQL`**, creates the
   **`DispatchLog`** database, and grants `NT AUTHORITY\SYSTEM` sysadmin (the service runs as LocalSystem and
   connects over shared memory with Windows auth). Skipped if the instance already exists.
2. **Dispatch MSI** (`Dispatch.wxs`) — installs the binaries + Windows service + firewall rules and writes
   `%ProgramData%\Dispatch\appsettings.json` pointing at the local instance (via the `WriteAppSettings`
   custom action). The admin password is set on first run in the dashboard.

This pattern is adapted from the FluxDeploy installer (`packaging/sql-express-launcher`, `relay-bundle`).

Build (Windows, .NET SDK + WiX v6 toolset):

```powershell
cd installer\windows
dotnet publish ..\..\src\Dispatch.Service -c Release -o publish      # (build + embed the UI first)
wix build Dispatch.wxs -d PublishDir=publish -ext WixToolset.Firewall.wixext -o Dispatch.msi
dotnet build sql-express\SqlExpressLauncher.csproj -c Release        # -> InstallSqlExpress.exe
wix build bundle\Bundle.wxs -ext WixToolset.BootstrapperApplications.wixext -ext WixToolset.Util.wixext `
  -bindpath:SqlLauncher=sql-express\bin\Release\net472 -bindpath:Msi=. -o DispatchSetup.exe
```

To use an **existing** SQL Server instead of the local Express install, install the MSI directly and pass a
connection string: `msiexec /i Dispatch.msi SQLCONN="Server=...;Database=DispatchLog;User Id=...;Password=...;TrustServerCertificate=True;Encrypt=True"`.

### 2. MSI only (`windows/Dispatch.wxs`)

Binaries + service + firewall + `appsettings.json`. Defaults `SQLCONN` to the local `DISPATCHSQL` instance;
override via the `SQLCONN` property for an existing server.

### 3. Script (`windows/install.ps1`)

Elevated PowerShell scripted install (publishes, writes config, registers the service, opens firewall) for a
pre-existing SQL Server. Requires the .NET SDK + Node.js on the host.

## Linux (`linux/`)

`install.sh` publishes the service, lays out `/opt/dispatch`, `/etc/dispatch/appsettings.json` (mode 600),
`/var/lib/dispatch/spool`, `/var/log/dispatch`, creates the `dispatch` service account, and installs the
`dispatch.service` systemd unit. It can either use an existing SQL Server or install one locally.

```bash
# Existing SQL Server / Azure SQL:
sudo ./linux/install.sh \
  --sql-connection "Server=...;Database=DispatchLog;User Id=...;Password=...;TrustServerCertificate=True;Encrypt=True" \
  --admin-password "<dashboard admin password>"

# Or install SQL Server Express locally (Ubuntu/Debian or RHEL/Fedora) + a self-signed dashboard cert:
sudo ./linux/install.sh --install-sql --sa-password "<StrongSaPassw0rd!>" \
  --admin-password "<dashboard admin password>" --generate-cert
```

`--install-sql` adds Microsoft's `mssql-server` package (Express edition = free, via `MSSQL_PID=Express`),
runs the unattended setup, and creates the `DispatchLog` database. `--generate-cert` creates a self-signed
PFX and serves the dashboard over HTTPS (spec §17.2). Prerequisites: the .NET 10 SDK and Node.js (the script
builds the embedded UI and publishes); a prebuilt self-contained tarball is future work.

## Validation status

The Windows artifacts (PowerShell bootstrap, WiX MSI/bundle, the net472 launcher) and the Linux
`--install-sql` path were authored on macOS and **must be validated on the target OS** — they cannot be
built or executed here. The Linux `install.sh` passes `bash -n` syntax checks. The SQL-Express bootstrap and
bundle-chaining approach mirror the proven FluxDeploy installer.
