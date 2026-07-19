# Installers

Per spec §12.1, the only things written to the platform config file are the **PostgreSQL connection string**, the
install-time **admin-password seed**, and (optionally) the **Web UI TLS cert**. Everything else (ports,
spool, retry, retention, …) is seeded into the PostgreSQL `config` table on first run and managed from the
dashboard. On first start the admin-password seed is hashed (bcrypt) into the `config` table; if you omit
it, the dashboard shows a one-time first-run setup screen instead.

## Windows (`windows/`)

Three ways to install, from most to least automated:

### 1. Bundle (`windows/bundle/Bundle.wxs`) - recommended

A WiX **Burn bundle** (`DispatchSetup.exe`) chaining the **Dispatch MSI** (`Dispatch.wxs`), which installs
the binaries, Windows service and firewall rules, and writes `%ProgramData%\Dispatch\appsettings.json` via
the `WriteAppSettings` custom action. The admin password is set on first run in the dashboard.

There is no database step. Dispatch defaults to a bundled **SQLite** file under `%ProgramData%\Dispatch`,
which the service creates on first start. The installer used to download and silently install a PostgreSQL
server - the slowest and most failure-prone part of a Windows install, taking 15-20 minutes and leaving a
database service behind after uninstall.

Build (Windows, .NET SDK + WiX v6 toolset):

```powershell
cd installer\windows
dotnet publish ..\..\src\Dispatch.Service -c Release -o publish      # (build + embed the UI first)
wix build Dispatch.wxs -d PublishDir=publish -ext WixToolset.Firewall.wixext -o Dispatch.msi
wix build bundle\Bundle.wxs -ext WixToolset.BootstrapperApplications.wixext -ext WixToolset.Util.wixext `
  -bindpath "Msi=." -o DispatchSetup.exe
```

To use a database **server you already run**, pass a connection string:
`msiexec /i Dispatch.msi SQLCONN="Host=...;Database=DispatchLog;Username=...;Password=..."`. Add
`DBPROVIDER=SqlServer` (or `MySql`) when the connection string could target either engine. See
[docs/database.md](../docs/database.md).

### 2. MSI only (`windows/Dispatch.wxs`)

Binaries + service + firewall + `appsettings.json`. Defaults `SQLCONN` to the local bundled PostgreSQL
instance; override via the `SQLCONN` property for an existing server.

### 3. Script (`windows/install.ps1`)

Elevated PowerShell scripted install (publishes, writes config, registers the service, opens firewall) for a
pre-existing PostgreSQL. Requires the .NET SDK + Node.js on the host.

## Linux (`linux/`)

`install.sh` publishes the service, lays out `/opt/dispatch`, `/etc/dispatch/appsettings.json` (mode 600),
`/var/lib/dispatch/spool`, `/var/log/dispatch`, creates the `dispatch` service account, and installs the
`dispatch.service` systemd unit. It can either use an existing PostgreSQL or install one locally.

```bash
# Existing PostgreSQL:
sudo ./linux/install.sh \
  --sql-connection "Host=...;Port=5432;Database=DispatchLog;Username=dispatch;Password=..." \
  --admin-password "<dashboard admin password>"

# Or take the default - bundled SQLite, no database server - with a self-signed dashboard cert:
sudo ./linux/install.sh --admin-password "<dashboard admin password>" --generate-cert
```

Omitting `--sql-connection` uses the bundled SQLite database at `/var/lib/dispatch/dispatch.db`.
`--generate-cert` creates a self-signed
PFX and serves the dashboard over HTTPS (spec §17.2). Prerequisites: the .NET 10 SDK and Node.js (the script
builds the embedded UI and publishes); a prebuilt self-contained tarball is future work.

## Validation status

The Windows artifacts (PowerShell bootstrap, WiX MSI/bundle, the net472 launcher) and the Linux
installer scripts were authored on macOS and **must be validated on the target OS** - they cannot be
built or executed here. The Linux `install.sh` passes `bash -n` syntax checks, and CI runs a full
tarball install + /health smoke on Ubuntu.
