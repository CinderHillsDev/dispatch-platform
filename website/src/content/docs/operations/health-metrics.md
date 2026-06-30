---
title: Health & metrics
description: The /health endpoint and Prometheus /metrics, including how access to them is controlled.
sidebar:
  order: 1
---

Dispatch exposes two unauthenticated operational endpoints, both **only on the dashboard port**
(`8420`) and gated by the dashboard's IP allow-list.

## `GET /health`

Returns JSON describing service health - overall status, version, uptime, database reachability,
free disk, and spool state (incoming/processing/failed counts). Returns `200` when healthy or
degraded and `503` when critical (e.g. disk critically low). It's best-effort and non-blocking, so it
never hangs.

The `smtp` object reports both `configuredPorts` (what's set in `listener.ports`) and `listeningPorts`
(what actually bound). These differ when port 25 couldn't be bound and the listener
[fell back to 2525](/sending/smtp/) - so `listeningPorts` is the source of truth for where mail is
accepted. (`ports` is kept as an alias of the configured set for backward compatibility.)

```bash
curl -k https://localhost:8420/health
```

## `GET /metrics`

Prometheus text exposition format (`version=0.0.4`). Gauges include:

- `dispatch_up`, `dispatch_database_reachable`
- `dispatch_messages_today{event="received|delivered|failed|retried|denied"}`
- `dispatch_messages_last5m{event="received|sent"}`
- `dispatch_spool_files{state="incoming|processing|failed"}`
- `dispatch_relay_inflight{relay="…"}`, `dispatch_relay_delivered_today{…}`, `dispatch_relay_failed_today{…}`

## Access control

Both endpoints are **unauthenticated** (no login, no token) - standard for Prometheus scrapers - but
they are **not** wide open:

1. Served **only on the dashboard port**; a request on the SMTP or ingestion port gets `404`.
2. They pass through the dashboard's **`webui.allowed_cidrs`** source-IP allow-list, so locking that
   to your monitoring host/subnet locks down `/metrics` and `/health` too.

The dashboard allow-list **defaults to allow-all** (it's password-gated for the UI), so out of the
box anyone who can reach port `8420` can scrape `/metrics`. It exposes only operational counters -
**no secrets, no message content, no addresses** (relay *names* and volumes are the only mildly
sensitive data). If you expose the dashboard port beyond a trusted network, restrict
`webui.allowed_cidrs` to your Prometheus host. See [Security](/security/).
