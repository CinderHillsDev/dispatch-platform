---
title: HTTP API
description: Submit mail over a Mailgun-compatible HTTP/HTTPS API with per-key tokens.
sidebar:
  order: 2
---

If you'd rather send over HTTP than SMTP, Dispatch exposes an ingestion API on port **8025** (plain
HTTP) with an optional **HTTPS** listener on **8026**. It's intentionally shaped like Mailgun's
`/messages` endpoint.

## Authentication

Create a key under **Settings → API Keys** (prefixed `dsp_live_…`) and send it as a bearer token.
Keys are stored as bcrypt hashes, verified in constant time, and rate-limited per key (default 100
requests/minute). Revocation is immediate. See [Security](/security/).

## Submit a message

```bash
curl -X POST http://localhost:8025/api/v1/messages \
  -H "Authorization: Bearer dsp_live_your_key_here" \
  -F from="App <noreply@myapp.com>" \
  -F to="user@example.com" \
  -F cc="team@example.com" \
  -F subject="Hello" \
  -F text="Hello world" \
  -F html="<p>Hello world</p>" \
  -F attachment=@./invoice.pdf
# → 202 Accepted: { "id": "spl_a1b2c3d4", "message": "Queued. Thank you." }
```

- **`multipart/form-data`** — fields `from`, `to`, `cc`, `bcc`, `subject`, `text`, `html`,
  `h:<Name>` (custom headers), `attachment` (repeatable), `o:tag` (repeatable).
- **`application/json`** — `{ from, to[], cc[], bcc[], subject, text, html, headers{}, tags[] }`.

## Check status / history

```bash
GET /api/v1/messages/{id}          # status of one message
GET /api/v1/messages?limit=50      # recent messages for the calling key
```

Status values: `queued` → `processing` → `delivered` | `retrying` | `failed`.

## HTTPS

Enable the HTTPS listener under **Settings → HTTP API** (port 8026). It uses the shared
[TLS certificate](/configuration/tls-certificate/) that also secures SMTP STARTTLS — and if you
haven't configured one, it falls back to an auto-generated self-signed cert, so HTTPS works as soon as
you flip the toggle. Once clients are migrated you can turn the plain-HTTP listener off entirely. The API is gated by API keys and a
source-IP allow-list (closed to private ranges by default) — see [Security](/security/).
