---
title: Configuration model
description: How Dispatch stores settings - almost everything in SQL, only a few bootstrap items in appsettings.
sidebar:
  order: 1
---

Dispatch keeps **almost all settings in the SQL `config` table**, managed through the dashboard and
seeded with sensible defaults on first run. Only a few **bootstrap** items live in
`appsettings.json`, because they're needed before the database is available.

## Bootstrap items (appsettings.json)

- `ConnectionStrings:DispatchLog` - the SQL connection string (required).
- The first-run **admin password** (seeded once, then stored only as a bcrypt hash in SQL).
- The dashboard **TLS certificate** path/password (optional; otherwise a self-signed cert is
  generated).
- The **log directory**.

Everything else - ports, allow-lists, provider credentials, retry policy, retention - is in the SQL
config table. Secrets there (provider API keys, SMTP passwords, TLS cert password) are
[encrypted at rest](/security/).

## Settings groups

- **Connections** - the [SMTP listener](/sending/smtp/), the [HTTP API](/sending/http-api/), and the
  dashboard (ports, allow-lists, toggles).
- **Relay provider** - upstream [provider](/providers/overview/) credentials.
- **[TLS certificate](/configuration/tls-certificate/)** - the shared cert for SMTP STARTTLS + HTTPS API.
- **[Retention & purge](/configuration/retention-purge/)** - how long history and spool files are kept.
- **SMTP authentication** - username/password pairs for SMTP AUTH.
- **Spool** - directory and worker count.

See the [config key reference](/configuration/config-keys/) for every key, its default, and whether
changing it needs a restart.
