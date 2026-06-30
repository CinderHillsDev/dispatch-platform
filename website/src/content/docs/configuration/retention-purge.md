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
| Delivered log rows | 7 days |
| Failed log rows | 7 days |
| Spool `failed/` files | 7 days |
| Captured (Local Inbox) files | 7 days |
| Audit log | 7 days |
| Security audit entries | 7 days |

All of these are editable under **Settings → Retention**, and map to the `purge.*` keys in the [config key reference](/configuration/config-keys/).

Setting any retention to **`0` means "keep forever"** — that data type is never purged by age (the database size safety net below still applies). Use `1` or more to actually delete by age.

## Storage usage

**Settings → Storage → Usage** (and `GET /api/storage`) shows what each retention category is actually consuming: message-log rows broken out by event (Delivered, Failed, Retrying, Test sends, Denied) with an estimated size, the audit log's rows and size, and the on-disk spool directories (captured / retry-queue) with exact file counts and bytes — plus free space on the spool volume. Use it to decide which retention window to tighten. (Per-event database sizes are estimated from each event's share of the message-log table; spool figures are exact.)

## Size-based safety

In addition to age-based purging, Dispatch watches the database size **on SQL Server Express only** — Express caps each database's data files at **10 GB**. When usage approaches the cap, Dispatch frees a bounded amount of space (down to the target you set) by removing the **oldest** rows — message-log rows first, then audit rows if needed.

Because this is an emergency safety net that runs **even when a retention is set to "keep forever"**, it does **not** silently discard history: before deleting, it **exports the rows to weekly JSON Lines files** (e.g. `relay_log-2026-W26.jsonl`, `audit_log-2026-W26.jsonl`) under the spool's `archive/` directory. The exact counts are written to the System Logs as a "Size-pressure cleanup ran" entry. You can move those archive files off the box or re-ingest them later.

If you point Dispatch at your **own SQL Server** (Standard/Enterprise/Azure) there is no 10 GB cap, so this size-based purge — and the archiving — never runs; only your age-based retention applies.

## Spool disk protection

Separately, Dispatch watches free disk space on the spool volume. As free space runs low it first **throttles**, then temporarily **refuses new SMTP intake** with a transient `4xx` response. Because the rejection is transient, well-behaved senders simply retry, and intake resumes automatically once space recovers. This ensures a full disk can never corrupt the spool.
