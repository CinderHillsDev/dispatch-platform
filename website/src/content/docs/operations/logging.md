---
title: Logging
description: Where Dispatch writes application logs and the SQL audit log, plus per-platform locations.
sidebar:
  order: 2
---

Dispatch keeps two distinct logs: the **application log** (Serilog) for operational diagnostics, and an
**audit log** in the database for security and configuration events. The relay log (per-message delivery
history) is separate and covered under message handling.

## Application log

Serilog writes to the console and to rolling daily files at `logs/dispatch-*.log`, with roughly
**31-day** retention. The default level is **Information**. The log directory is a bootstrap setting
(configured outside the database).

Per-platform locations:

- **Linux:** follow the journal with `sudo journalctl -u dispatch -f`.
- **Windows:** files live under `C:\ProgramData\Dispatch\logs`.

## Audit log

A separate **audit log** is stored in the SQL `audit_log` table and records security and configuration
events. It's viewable under **System Logs** in the dashboard.

- **Categories:** Auth, Config, ApiKey, Relay, System
- **Severities:** Info, Notice, Warning

Because the audit log lives in the database, it's included in your database backups and survives across
service restarts.

## See also

- [Health & metrics](/operations/health-metrics/)
- [Security](/security/)
