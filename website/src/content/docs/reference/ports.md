---
title: Ports & defaults
description: Default ports and addresses for every Dispatch listener.
sidebar:
  order: 2
---

| Service | Default port | Protocol | Notes |
|---|---|---|---|
| SMTP listener | `25`, `587` | SMTP (plaintext / STARTTLS) | Falls back to `2525` if 25 is in use or unprivileged |
| HTTP ingestion API | `8025` | HTTP | API-key gated; can be disabled once on HTTPS |
| HTTPS ingestion API | `8026` | HTTPS | Off by default; uses the shared TLS cert |
| Dashboard | `8420` | HTTPS | Self-signed by default; HTTPS-only |
| Spool directory | `./.dispatch-spool` | Local disk | Configurable; relative to app root |

All listeners bind `0.0.0.0` (all interfaces) and are filtered by their
[CIDR allow-lists](/security/). The `/health` and `/metrics` endpoints are served on the dashboard
port only - see [Health & metrics](/operations/health-metrics/).
