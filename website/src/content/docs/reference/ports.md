---
title: Ports & defaults
description: Default ports and addresses for every Dispatch listener.
sidebar:
  order: 2
---

| Service | Default port | Protocol | Notes |
|---|---|---|---|
| SMTP listener | `2525` | SMTP (plaintext / STARTTLS) | Set `25`/`587` for production |
| HTTP ingestion API | `8025` | HTTP | API-key gated; can be disabled once on HTTPS |
| HTTPS ingestion API | `8026` | HTTPS | Off by default; uses the shared TLS cert |
| Dashboard | `8420` | HTTPS | Self-signed by default; HTTPS-only |
| Spool directory | `./.dispatch-spool` | Local disk | Configurable; relative to app root |

All listeners bind `0.0.0.0` (all interfaces) and are filtered by their
[CIDR allow-lists](/security/). The `/health` and `/metrics` endpoints are served on the dashboard
port only — see [Health & metrics](/operations/health-metrics/).
