# Installers

Per spec §12.1, the only things written to the platform config file are the **PostgreSQL connection string**, the
install-time **admin-password seed**, and (optionally) the **Web UI TLS cert**. Everything else (ports,
spool, retry, retention, …) is seeded into the PostgreSQL `config` table on first run and managed from the
dashboard. On first start the admin-password seed is hashed (bcrypt) into the `config` table; if you omit
it, the dashboard shows a one-time first-run setup screen instead.

## Windows (`windows/`)

Three ways to install, from most to least automated:

### 1. Bundle (`windows/bundle/Bundle.wxs`) - recommended

A WiX **Burn bundle** (`DispatchSetup.exe`) that chains, in order:

1. **PostgreSQL** - `postgres/InstallPostgres.exe` runs `InstallPostgres.ps1`, which downloads
   and silently installs a bundled PostgreSQL server, creates the **`DispatchLog`** database and a
   least-privilege **`dispatch`** role, and configures local access for the service. Skipped if a
   Dispatch PostgreSQL install already exists.
2. **Dispatch MSI** (`Dispatch.wxs`) - installs the binaries + Windows service + firewall rules and writes
   `%ProgramData%\Dispatch\appsettings.json` pointing at the local instance (via the `WriteAppSettings`
   custom action). The admin password is set on first run in the dashboard.

This pattern is adapted from the FluxDeploy installer (`packaging/postgres-launcher`, `relay-bundle`).

Build (Windows, .NET SDK + WiX v6 toolset):

```powershell
cd installer\windows
dotnet publish ..\..\src\Dispatch.Service -c Release -o publish      # (build + embed the UI first)
wix build Dispatch.wxs -d PublishDir=publish -ext WixToolset.Firewall.wixext -o Dispatch.msi
dotnet build postgres\PostgresLauncher.csproj -c Release             # -> InstallPostgres.exe
wix build bundle\Bundle.wxs -ext WixToolset.BootstrapperApplications.wixext -ext WixToolset.Util.wixext `
  -bindpath "PostgresLauncher=postgres\bin\Release\net472" -bindpath "Msi=." -o DispatchSetup.exe
```

To use an **existing** PostgreSQL instead of the bundled install, install the MSI directly and pass a
connection string: `msiexec /i Dispatch.msi SQLCONN="Host=localhost;Port=5432;Database=DispatchLog;Username=dispatch;Password=..."`.

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

# Or install PostgreSQL locally (Ubuntu/Debian or RHEL/Fedora) + a self-signed dashboard cert:
sudo ./linux/install.sh --install-postgres --db-password "<StrongDbPassw0rd!>" \
  --admin-password "<dashboard admin password>" --generate-cert
```

`--install-postgres` adds the distribution's `postgresql` package, runs the unattended setup, and creates
the `DispatchLog` database and a least-privilege `dispatch` role. `--generate-cert` creates a self-signed
PFX and serves the dashboard over HTTPS (spec §17.2). Prerequisites: the .NET 10 SDK and Node.js (the script
builds the embedded UI and publishes); a prebuilt self-contained tarball is future work.

## Validation status

The Windows artifacts (PowerShell bootstrap, WiX MSI/bundle, the net472 launcher) and the Linux
`--install-postgres` path were authored on macOS and **must be validated on the target OS** - they cannot be
built or executed here. The Linux `install.sh` passes `bash -n` syntax checks. The PostgreSQL bootstrap and
bundle-chaining approach mirror the proven FluxDeploy installer.
