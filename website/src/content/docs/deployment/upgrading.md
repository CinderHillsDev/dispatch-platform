---
title: Upgrading
description: Upgrade Dispatch from the web dashboard by uploading a signed package, or in place per platform. Schema migrations are additive and automatic.
sidebar:
  order: 6
---

Dispatch supports in-place upgrades on every platform. Schema migrations are **additive** and applied
automatically on startup, so your config, all message history, and the durable spool are preserved
across the restart.

## From the dashboard (recommended)

Appliance, Linux, and Windows installs can update themselves from the web UI by uploading one signed
**upgrade package** - no shell access needed.

1. Download the package for the release you want from the
   [GitHub Releases](https://github.com/chrismuench/Dispatch-SMTP-Relay/releases) page:
   **`dispatch-upgrade-<version>.tar.gz`**. It's a single, cross-platform file - the same one works on
   every appliance, Linux, and Windows host (it carries each platform's payload inside).
2. In the dashboard, go to **Updates** (under *System*).
3. Choose the package file and click **Upload & apply**.

Dispatch then:

- **verifies** the package - it checks the manifest's signature against the release key built into the
  app and the payload's SHA-256, and refuses anything that doesn't match (so a tampered or unofficial
  package can't be applied);
- selects the payload for this host's architecture, swaps to the new version, and **restarts** (the
  dashboard blinks for a few seconds - the Updates page reconnects on its own and shows progress); and
- **automatically rolls back** to the previous version if the new one fails to start.

The page shows the live state: `Staged -> Applying -> Succeeded` (or `RolledBack`). A pre-upgrade
database backup is taken where possible; because migrations are additive, the rolled-back old binaries
keep working against the upgraded schema.

:::note
The dashboard upgrade is available on self-managed installs (appliance / Linux / Windows). **Docker**
installs update by pulling a new image (below) - the Updates page will say so.
:::

## Manual / per-platform

### Windows

Run the new `DispatchSetup` exe. The embedded MSI performs a `MajorUpgrade`: it stops the service,
replaces the binaries, and restarts.

### Linux

Download the new tarball and re-run the installer with your existing connection string:

```bash
sudo ./install.sh \
  --sql-connection 'Server=YOURHOST;Database=DispatchLog;User Id=sa;Password=***;TrustServerCertificate=True;Encrypt=True'
```

It installs the new release under `/opt/dispatch/releases/<version>`, repoints the `current` symlink,
and restarts the service.

### Docker

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

The dashboard upgrade auto-rolls-back the **binaries** if the new version won't start, and takes a
best-effort database backup beforehand. Migrations are additive, but database rollback is not automatic -
for production, take a database backup before any upgrade. See [Backup & restore](/operations/backup-restore/).
