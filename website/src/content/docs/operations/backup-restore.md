---
title: Backup & restore
description: What to back up in Dispatch SMTP Relay — the SQL database, encryption key, and config — and how to restore.
sidebar:
  order: 3
---

A complete Dispatch backup is small: the database holds almost everything, plus an encryption key and
the bootstrap config file. Get all three and you can restore to a fresh host.

## What to back up

1. **The SQL `DispatchLog` database** — all config, message history, API keys, routing rules, and the
   audit log. Use SQL Server backup or Azure SQL automated backups.
2. **The encryption key file** `.dispatch-key`. **Without it, encrypted secrets cannot be decrypted
   after a restore** — provider keys, SMTP passwords, and the TLS certificate password. It's a portable
   file on every platform, so restoring it alongside the database lets you move to a **different
   machine**:
   - **Linux/macOS** — mode `600`, in the key/app directory (or `DISPATCH_KEY_DIR`).
   - **Windows** — in the Program Files install dir (`C:\Program Files\Dispatch\.dispatch-key`), locked
     down with an ACL. (Older builds used DPAPI, which was machine-bound; those values migrate to the
     portable key on the next save.)
3. **`appsettings.json`** — the connection string and the dashboard TLS certificate.

The **spool directory** holds only in-flight and captured mail; drain it before planned maintenance
rather than relying on a backup of it.

## Restore

1. Provision SQL Server (or Azure SQL).
2. Restore the `DispatchLog` database.
3. Put `.dispatch-key` and `appsettings.json` back in place.
4. Start the service — the schema auto-migrates if needed.

## Before upgrades

Always take a database backup before upgrading production. See [Upgrading](/deployment/upgrading/) for
the upgrade flow, and [Security](/security/) for how secrets are encrypted at rest.
