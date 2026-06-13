# Installers

Both installers collect the **SQL connection string** and the **dashboard admin password** at install
time and write them to the platform config file *before the service first starts*. On first start the
admin password is hashed (bcrypt) into the SQL `config` table; the dashboard is not accessible until a
password exists (set here, or via the one-time first-run screen).

## Linux (`linux/`)

`install.sh` publishes the service, lays out `/opt/dispatch` (binaries), `/etc/dispatch/appsettings.json`
(mode 600), `/var/lib/dispatch/spool`, `/var/log/dispatch`, creates a `dispatch` service account, and
installs the `dispatch.service` systemd unit.

```bash
sudo ./linux/install.sh \
  --sql-connection "Server=localhost,1433;Database=DispatchLog;User Id=sa;Password=...;TrustServerCertificate=True;Encrypt=True" \
  --admin-password "<dashboard admin password>"
```

Prerequisites on the host: the .NET 10 SDK and Node.js (the script builds the embedded UI and publishes).
A future iteration can ship a prebuilt self-contained tarball to drop both prerequisites.

## Windows (`windows/`)

- **`install.ps1`** — scripted install (elevated PowerShell): publishes to `%ProgramFiles%\Dispatch`,
  writes `%ProgramData%\Dispatch\appsettings.json`, registers the `Dispatch` Windows service
  (`--contentRoot %ProgramData%\Dispatch`), and opens firewall ports.
- **`Dispatch.wxs`** — WiX v5 MSI source for packaged installs (binaries + service + firewall). Build on
  Windows with `wix build` against a published output directory.

> Status: the Windows artifacts were authored on macOS and have **not** been executed/built here; they
> should be validated on Windows. The full WinForms bootstrap wizard (SQL detect/install, schema
> migration UI) described in the spec (§15) is future work — `install.ps1` covers the same inputs today.
