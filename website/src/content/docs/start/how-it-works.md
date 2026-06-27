---
title: How it works
description: The spool-first pipeline that lets Dispatch acknowledge mail instantly and survive outages.
sidebar:
  order: 3
---

Dispatch is built around a **spool-first** pipeline: it accepts a message, writes it to a local file,
and acknowledges the sender *before* doing anything that could be slow or fail.

```
Your apps / devices  →  Dispatch SMTP (port 25/587/2525)  ─┐
                                                            ├→  Provider (Mailgun / SES / SMTP / …)
Your apps / scripts  →  Dispatch API  (port 8025/8026)    ─┘
         ↑                       ↕
   202 / 250 OK instantly    spool directory
   (before any DB or         (durable in-flight queue)
    network call)                  ↕
                              relay_log in SQL
                              (after-the-fact history)
                                    ↕
                             Web UI (port 8420)
                         Configure · Monitor · Debug
```

## The flow

1. **Ingest** — a message arrives over SMTP or the HTTP API. After source-IP and auth checks, it is
   written to `spool/incoming/` as an `.eml` file with a `.meta` sidecar.
2. **Acknowledge** — Dispatch returns `250 OK` (SMTP) or `202 Accepted` (API) immediately. Nothing
   on the hot path touches the database or the network.
3. **Relay** — a worker picks up the file, resolves the [routing rule](/routing/) to choose a relay,
   and forwards it to the provider. On success it moves on; on a transient error it retries with
   back-off; on permanent failure it lands in `spool/failed/` for manual retry.
4. **Log** — the outcome is written to the `relay_log` table in SQL Server for the searchable history
   in the dashboard.

## Why it matters

- **Resilience** — the spool is the source of truth. If SQL Server or the provider is down, mail is
  still accepted and delivered once they recover.
- **Speed** — senders never wait on a database write or an upstream API call.
- **Durability** — `.eml` files survive process restarts and crashes.

See [Configuration](/configuration/overview/) for the spool directory, worker count, and retry
policy.
