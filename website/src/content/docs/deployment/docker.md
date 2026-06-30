---
title: Docker
description: Run Dispatch SMTP Relay from a multi-arch container image, locally with Compose or from GHCR against your own SQL Server.
sidebar:
  order: 2
---

Dispatch ships as a multi-arch container image (`linux/amd64` + `linux/arm64`), so the same tag runs
natively on x86 servers and Apple Silicon. There are two common paths: a one-command local stack with
Compose, or running the published image against your own SQL Server.

## Try it locally with Compose

From a clone of the repository, bring up Dispatch and its database together. The Compose file uses
**Azure SQL Edge** (arm64-native) so it works the same on any host:

```bash
docker compose up -d --build
```

The dashboard comes up at **https://localhost:8420** with a self-signed certificate (accept the browser
warning). The default login password is in `docker-compose.yml` - **change it after your first login**.

## Run from GHCR

Point the published image at your own SQL Server or Azure SQL:

```bash
docker run -d --name dispatch \
  -p 8420:8420 -p 8025:8025 -p 25:25 -p 587:587 \
  -e ConnectionStrings__DispatchLog="Server=<host>,1433;Database=DispatchLog;User Id=sa;Password=<pw>;TrustServerCertificate=True;Encrypt=True" \
  -e AdminPassword="<DashboardPassword!>" \
  -v dispatch-spool:/app/.dispatch-spool \
  ghcr.io/chrismuench/dispatch-smtp-relay:latest
```

Only two settings are passed in: the **SQL connection string** and the **first-run admin password**.
Everything else is seeded into the SQL config table and managed from the dashboard - see
[Configuration](/configuration/overview/). The `DispatchLog` schema is created and migrated
automatically on first start.

## Ports

| Port | Purpose |
|---|---|
| 8420 | Dashboard (HTTPS) |
| 8025 | HTTP API |
| 25, 587 | SMTP listener (the container runs as root, so it binds the standard ports; if host 25 is busy, remap e.g. `-p 2525:25`) |

## Volumes

| Volume | Purpose |
|---|---|
| `dispatch-spool` (`/app/.dispatch-spool`) | Durable message queue - persists in-flight and captured mail across restarts |

## Allow-lists

The default source-IP allow-lists are container-aware: the dashboard and API allow all connections and
are gated by the password and API keys, while the SMTP listener accepts only loopback and private
(RFC1918) ranges so it isn't an open relay. Tighten these in **Settings** - see
[Security](/security/).
