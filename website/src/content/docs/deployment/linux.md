---
title: Linux
description: Install Dispatch SMTP Relay on Linux from a self-contained tarball with the bundled install.sh script.
sidebar:
  order: 3
---

Download `dispatch-{version}-linux.tar.gz` from the
[latest release](https://github.com/chrismuench/Dispatch-SMTP-Relay/releases/latest). It's one
universal, self-contained bundle (x64 + arm64, no .NET SDK required) - `install.sh` auto-detects your
CPU architecture.

## Install

```bash
tar xzf dispatch-*-linux.tar.gz && cd dispatch-*-linux

# With a bundled database (SQL Server 2025 Express on x64; Azure SQL Edge container on arm64):
sudo ./install.sh --install-sql \
  --sa-password '<StrongSaPassw0rd!>' --admin-password '<DashboardPassword!>'

# ...or against an existing SQL Server / Azure SQL:
sudo ./install.sh \
  --sql-connection 'Server=YOURHOST;Database=DispatchLog;User Id=sa;Password=***;TrustServerCertificate=True;Encrypt=True' \
  --admin-password '<DashboardPassword!>'
```

The script lays out `/opt/dispatch`, writes the config, installs a `systemd` unit, and starts the
service.

## Architecture note

SQL Server has no arm64 Linux build. On arm64, `--install-sql` uses an **Azure SQL Edge** container
(which needs Docker installed); alternatively point `--sql-connection` at an external instance.

## Password policy

Passwords must be **12+ characters** with an uppercase letter, a lowercase letter, and a digit. Omit
`--admin-password` to be prompted for it interactively rather than passing it on the command line.

## Check the service

```bash
sudo systemctl status dispatch
sudo journalctl -u dispatch -f
```

For more on what gets written where, see [Logging](/operations/logging/). Review the default
allow-lists and harden your install via [Security](/security/).
