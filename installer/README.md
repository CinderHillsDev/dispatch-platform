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

Binaries + service + firewall + `appsettings.json`. With `SQLCONN` unset it uses the bundled SQLite
database under `%ProgramData%\Dispatch`; set `SQLCONN` to point at a database server you already run, and
`DBPROVIDER` when the connection string could target either SQL Server or MySQL.

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
builds the embedded UI and publishes). From a release tarball neither is needed - pass `--prebuilt <dir>`, or
let `install.sh` pick the arch payload out of the tarball it shipped in.

## macOS (`macos/`)

`install.sh` is the same script with the systemd parts replaced by `launchd`: it lays out `/opt/dispatch`
(release symlink + binaries), `/var/db/dispatch` (`appsettings.json` mode 600, the SQLite database, the spool,
`.dispatch-key`), `/var/log/dispatch`, and installs two LaunchDaemons - `app.dispatchrelay.dispatch.plist`
(the service) and `app.dispatchrelay.dispatch-update.plist` (the `WatchPaths` job that applies staged
updates as root).

Most people should not run this script at all: open the release `.dmg`, drag `DispatchRelay.app` to
Applications, and its menu bar item runs this exact script through one administrator prompt
(see `macos/app/README.md`). The terminal route is for headless machines and SSH.

```bash
# Mount the release file and install from the copy inside it (no SDK, no Node, Apple Silicon):
hdiutil attach dispatch-<ver>-macos.dmg -nobrowse
sudo "/Volumes/Dispatch"*/DispatchRelay.app/Contents/Resources/payload/install.sh \
     --admin-password "<dashboard admin password>"
hdiutil detach "/Volumes/Dispatch"*

# Against an existing database server, or as an unprivileged service account:
sudo .../install.sh --sql-connection "Host=...;Database=DispatchLog;Username=...;Password=..." --admin-password "<pw>"
sudo .../install.sh --user _dispatch --admin-password "<pw>"     # SMTP then listens on 2525

sudo /opt/dispatch/install.sh --uninstall                        # daemons + binaries removed, data kept
```

`build-app.sh` packages that directory as `DispatchRelay.app` inside a `.dmg`, which is the only macOS file
a release ships. The bundle carries `install.sh` rather than reimplementing it, because a second copy of the
install logic is how two installers for one product start to disagree - and a `.pkg` used to exist here for
exactly that reason; see `docs/macos.md` for why it was dropped.

Two differences from Linux worth knowing before you read the flags again:

- **Root by default.** macOS has no `CAP_NET_BIND_SERVICE`, so binding 25/587 needs root; `--user` opts out
  and the listener falls back to 2525. The updater daemon stays root either way.
- **`launchctl stop` does not stop it.** `KeepAlive` brings it straight back. Use
  `sudo launchctl bootout system/app.dispatchrelay.dispatch`.

The binaries and the app are ad-hoc signed, not notarized. `install.sh` clears the quarantine flag from what
it installs; if you copied the payload by hand, or Gatekeeper refused the app's first launch, clear it with
`xattr -dr com.apple.quarantine <dir-or-.app>` (or right-click the app, Open). See
[docs/macos.md](../docs/macos.md) for the full picture.

## Validation status

The Windows artifacts (PowerShell bootstrap, WiX MSI/bundle, the net472 launcher) and the Linux and macOS
installer scripts were authored on a different OS than they target, so **CI runs them on the target OS** -
that is what [`installers.yml`](../.github/workflows/installers.yml) is for: a full tarball install + /health
smoke on Ubuntu, and plist linting with `plutil` plus a real `launchd` install driven through the app's own
privileged path, an update-applier test that verifies, refuses and rolls back, and an uninstall, on a macOS
runner. The Linux `install.sh` also passes `bash -n` syntax checks. Anything not
covered by those jobs (notably the Windows upgrade smoke) still needs a manual check on the target.
