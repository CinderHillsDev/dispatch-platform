---
title: Config key reference
description: The full reference of settings stored in the Dispatch SQL config table, with defaults and restart requirements.
sidebar:
  order: 2
---

Almost all settings live in the SQL `config` table (see [Configuration overview](/configuration/overview/)). The table is seeded with defaults on first run and edited in the dashboard. This page is the full key reference.

In the **Restart?** column, "Yes" means changing the key requires a service restart to take effect; "No" means the change applies live.

## SMTP listener

| Key | Default | Restart? | Notes |
| --- | --- | --- | --- |
| `listener.ports` | `[25, 587]` | Yes | JSON int array; listener falls back to 2525 if 25 can't be bound (in use / no privilege) |
| `listener.server_name` | `"Dispatch"` | Yes | SMTP banner / HELO name |
| `listener.allowed_cidrs` | loopback + RFC1918 | No | JSON string array; closed by default (not an open relay) |
| `listener.max_message_bytes` | `26214400` (25 MiB) | No | per-message ceiling |
| `listener.require_auth` | `false` | No | require SMTP AUTH |
| `listener.allow_unsecure_auth` | `false` | No | allow AUTH before STARTTLS |
| `listener.connection_timeout_seconds` | `60` | No | idle connection timeout |
| `listener.max_connections` | `100` | No | concurrent connection cap |

## Shared TLS

See [TLS certificate](/configuration/tls-certificate/) for how to generate or upload the shared cert.

| Key | Default | Restart? | Notes |
| --- | --- | --- | --- |
| `tls.cert_path` | `""` | Yes | path to PFX |
| `tls.cert_password` | `""` (encrypted) | Yes | encrypted at rest |
| `tls.cert_source` | `""` | No | `"generated"` or `"uploaded"` |

## Spool

| Key | Default | Restart? | Notes |
| --- | --- | --- | --- |
| `spool.directory` | `"./.dispatch-spool"` | Yes | spool root directory |
| `spool.worker_count` | `4` | Yes | concurrent delivery workers |
| `spool.max_retries` | `3` | No | delivery attempts before failure |
| `spool.retry_delays_seconds` | `[30,300,1800]` | No | JSON array; exponential back-off |

## HTTP API

See [HTTP API](/sending/http-api/) for usage.

| Key | Default | Restart? | Notes |
| --- | --- | --- | --- |
| `api.enabled` | `true` | No | master toggle for the ingestion API |
| `api.port` | `8025` | Yes | plain HTTP |
| `api.http_enabled` | `true` | Yes | toggle plain HTTP |
| `api.tls_enabled` | `false` | Yes | enable HTTPS listener |
| `api.tls_port` | `8026` | Yes | HTTPS port (shared TLS cert) |
| `api.allowed_cidrs` | loopback + RFC1918 | No | closed by default |
| `api.max_message_bytes` | `26214400` (25 MiB) | No | per-message ceiling |
| `api.rate_limit_per_key` | `100` | No | requests/minute per API key |

## Web UI / dashboard

| Key | Default | Restart? | Notes |
| --- | --- | --- | --- |
| `webui.port` | `8420` | Yes | HTTPS-only |
| `webui.allowed_cidrs` | `[]` (allow all) | No | open by default, password-gated |
| `webui.session_timeout_minutes` | `480` | No | 8 hours |
| `webui.require_https` | `true` | No | enforce HTTPS |

## Logging

| Key | Default | Restart? | Notes |
| --- | --- | --- | --- |
| `logging.log_delivered` | `true` | No | record delivered messages |
| `logging.log_retrying` | `true` | No | record retry attempts |
| `logging.log_denied` | `true` | No | record denied/rejected messages |

## Retention / purge

See [Retention & purge](/configuration/retention-purge/) for behavior.

| Key | Default | Restart? | Notes |
| --- | --- | --- | --- |
| `purge.enabled` | `true` | No | enable scheduled purge |
| `purge.schedule_interval_hours` | `6` | No | how often purge runs |
| `purge.spool_failed_retention_days` | `30` | No | failed spool file retention |
| `purge.captured_retention_days` | `7` | No | Local Inbox capture retention |
| `purge.log_delivered_retention_days` | `30` | No | delivered log row retention |
| `purge.log_failed_retention_days` | `90` | No | failed log row retention |
| `purge.audit_retention_days` | `90` | No | audit log retention |
| `purge.audit_security_retention_days` | `7` | No | security audit entry retention |
| `purge.size_trigger_gb` | `9.5` | No | start size-based purge at this DB size |
| `purge.size_target_gb` | `9.0` | No | purge down to this |

## Bootstrap-only settings

A few items cannot live in the `config` table because they are needed before the database is available, or because they secure the dashboard itself. The SQL connection string, the first-run admin password, the dashboard TLS certificate, and the log directory live in `appsettings.json` instead — see the [Configuration overview](/configuration/overview/).
