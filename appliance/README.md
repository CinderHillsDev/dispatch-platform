# Dispatch SMTP Relay — Hyper-V appliance

A ready-to-run **Ubuntu 24.04 LTS** virtual machine with Dispatch and SQL Server Express pre-installed. Download the VHDX, import it into Hyper-V, power it on, and the dashboard comes up — no .NET, SQL, or command line needed.

## Download & import (Hyper-V)

1. Download `dispatch-appliance-<version>-x64.vhdx.zip` from the [latest release](https://github.com/chrismuench/Dispatch-SMTP-Relay/releases/latest) and unzip the `.vhdx`.
2. Hyper-V Manager → **New → Virtual Machine**:
   - **Generation 2** (UEFI).
   - Memory: **4096 MB or more** (SQL Server Express needs ~2 GB).
   - Network: connect to a switch with DHCP (e.g. *Default Switch*).
   - **Use an existing virtual hard disk** → select the unzipped `.vhdx`.
3. VM → **Settings → Security → Secure Boot**: set the template to **Microsoft UEFI Certificate Authority** (required for Linux), or disable Secure Boot.
4. **Start** the VM.

## First boot

On first start the appliance gives itself a **unique** SQL SA password, configures and starts SQL Server Express, and starts Dispatch (the database and schema are created automatically). This takes a couple of minutes the first time.

Then browse to the dashboard:

```
https://<vm-ip>:8420
```

(The VM gets its IP via DHCP — see it in Hyper-V Manager or with `ip a` on the console.) It's a self-signed certificate, so accept the browser warning. **Set the admin password on the first login.**

Default ports: SMTP **2525**, ingestion API **8025**, dashboard **8420** — change them in the dashboard (e.g. SMTP 25/587 for production).

## Maintenance (console)

- Service: `systemctl status dispatch` · logs: `journalctl -u dispatch -f` and `/var/log/dispatch/`.
- SQL: `systemctl status mssql-server`.
- Config (connection string only): `/var/lib/dispatch/appsettings.json` — everything else is in the dashboard.

## Security notes

- Every appliance generates its **own** SQL SA password, at-rest encryption key (`.dispatch-key`), dashboard TLS cert, SSH host keys, and machine-id on first boot — nothing secret is shared across downloads.
- The admin password is set by **you** on first dashboard login and is stored only as a bcrypt hash in the database.
- The appliance bundles **SQL Server Express**, which is free; its use is governed by Microsoft's SQL Server Express license terms.

## Building it yourself

CI builds the VHDX in `.github/workflows/appliance.yml`. Locally, on a Linux host with `libguestfs-tools` + `qemu-utils`:

```bash
dotnet publish src/Dispatch.Service -c Release -r linux-x64 --self-contained true -o publish/linux
sudo PREBUILT_DIR="$PWD/publish/linux" VERSION=1.2.3 LIBGUESTFS_BACKEND=direct ./appliance/build-appliance.sh
```

It customizes the official Ubuntu cloud image offline (no Hyper-V host needed) and writes a Gen2/UEFI dynamic VHDX. See `build-appliance.sh`, `provision.sh` (in-guest build steps), and `firstboot.sh` (per-VM first-boot setup).
