---
title: Upgrading
description: In-place upgrades for Dispatch SMTP Relay on Windows, Linux, and Docker, with automatic additive schema migrations.
sidebar:
  order: 6
---

Dispatch supports in-place upgrades on every platform. Schema migrations are **additive** and applied
automatically on startup, so your config, all message history, and the durable spool are preserved
across the restart.

## Windows

Run the new `DispatchSetup` exe. The embedded MSI performs a `MajorUpgrade`: it stops the service,
replaces the binaries, and restarts.

## Linux

Download the new tarball and re-run the installer with your existing connection string:

```bash
sudo ./install.sh \
  --sql-connection 'Server=YOURHOST;Database=DispatchLog;User Id=sa;Password=***;TrustServerCertificate=True;Encrypt=True'
```

It republishes `/opt/dispatch` and restarts the service.

## Docker

Pull the new image tag and recreate the container:

```bash
docker pull ghcr.io/chrismuench/dispatch-smtp-relay:latest
docker compose up -d   # or re-run your docker run command
```

## Draining in-flight messages

To finish in-flight messages before the restart, drain the queue from **Settings**, or:

```bash
curl -X POST https://localhost:8025/api/service/drain
```

Drain waits up to **60 seconds** for queued messages to complete.

## Back up first

Automatic rollback is **not** implemented. Take a database backup before upgrading production - see
[Backup & restore](/operations/backup-restore/).
