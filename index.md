---
layout: default
title: Dispatch SMTP Relay
---

<p class="tagline">Self-hosted SMTP relay for .NET. Point every app at one place — deliver through any provider, with a durable spool and a live dashboard.</p>

<p class="actions">
<a class="btn" href="https://github.com/chrismuench/Dispatch-SMTP-Relay">★ GitHub</a>
<a class="btn" href="https://github.com/chrismuench/Dispatch-SMTP-Relay/releases/latest">⬇ Download</a>
<a class="btn" href="https://github.com/chrismuench/Dispatch-SMTP-Relay/blob/main/README.md">Docs</a>
</p>

![Dispatch dashboard](docs/images/dashboard.png)

## Why Dispatch

- **Instant `250 OK`** — messages land in a durable spool before any DB or network call.
- **Survives outages** — the `.eml` spool *is* the queue; mail keeps flowing even if SQL is down.
- **Any provider** — Mailgun · SendGrid · Amazon SES · Postmark · Resend · SparkPost · SMTP2GO · Azure · plain SMTP.
- **Two ways in** — SMTP (25 / 587 / 2525) and a Mailgun-compatible HTTP API.
- **Live dashboard** — counters, throughput, a searchable message log, and one-click provider testing.
- **Credentials in one place** — rotate a key once, not in every app.

## Quick start

```bash
docker run -d -p 8420:8420 -p 8421:8421 -p 2525:2525 \
  -e ConnectionStrings__DispatchLog="Server=…;Database=DispatchLog;…" \
  -e AdminPassword="choose-a-strong-one" \
  ghcr.io/chrismuench/dispatch-smtp-relay:latest
```

Open **`http://localhost:8420`**, add your provider credentials, and send. Prefer a native install? Grab the Windows or Linux installer from the [releases page](https://github.com/chrismuench/Dispatch-SMTP-Relay/releases/latest).

<p class="actions">
<a class="btn" href="https://github.com/chrismuench/Dispatch-SMTP-Relay/blob/main/README.md">Full documentation →</a>
</p>
