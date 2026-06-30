---
title: Retention & purge
description: How Dispatch ages out old logs, spool files, and captures, and how it protects the database and spool disk from filling up.
sidebar:
  order: 4
---

Dispatch automatically ages out old data so the database and spool disk stay healthy. A purge runs every 6 hours, and you can trigger one on demand from **Settings → Storage maintenance** or with `POST /api/purge/run`.

## Default retention

| Data | Retained for |
| --- | --- |
| Delivered log rows | 30 days |
| Failed log rows | 90 days |
| Spool `failed/` files | 30 days |
| Captured (Local Inbox) files | 7 days |
| Audit log | 90 days |
| Security audit entries | 7 days |

All of these are editable under **Settings → Retention**, and map to the `purge.*` keys in the [config key reference](/configuration/config-keys/).

Setting any retention to **`0` means "keep forever"** — that data type is never purged by age (the database size safety net below still applies). Use `1` or more to actually delete by age.

## Size-based safety

In addition to age-based purging, Dispatch watches the database file size. When the file reaches **9.5 GB** it purges the oldest log rows down to **9.0 GB**. This keeps the database comfortably below the **SQL Server Express 10 GB limit**, so the service never wedges on a full database.

## Spool disk protection

Separately, Dispatch watches free disk space on the spool volume. As free space runs low it first **throttles**, then temporarily **refuses new SMTP intake** with a transient `4xx` response. Because the rejection is transient, well-behaved senders simply retry, and intake resumes automatically once space recovers. This ensures a full disk can never corrupt the spool.
