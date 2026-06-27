---
title: HTTP API reference
description: Precise reference for the Dispatch HTTP ingestion API — endpoints, payloads, responses, and examples.
sidebar:
  order: 1
---

This is the reference for the Dispatch HTTP ingestion API. For a higher-level introduction, see [HTTP API](/sending/http-api/).

## Base URL and authentication

| | |
| --- | --- |
| Base (plain) | `http://<host>:8025` |
| Base (HTTPS, if enabled) | `https://<host>:8026` |
| Auth header | `Authorization: Bearer dsp_live_...` |
| Rate limit | per-key, default `100` requests/minute |

Create and revoke keys under **Settings → API Keys**. Each key carries its own rate limit (the `api.rate_limit_per_key` config value, default 100/min). See [Security](/security/) for handling keys safely.

The API is intentionally similar to Mailgun's `/messages` endpoint, so existing Mailgun integrations are easy to adapt.

## POST /api/v1/messages

Submit a message for delivery. Accepts either `multipart/form-data` or `application/json`.

### multipart/form-data fields

| Field | Notes |
| --- | --- |
| `from` | sender address |
| `to` | recipient address |
| `cc` | carbon copy address |
| `bcc` | blind carbon copy address |
| `subject` | message subject |
| `text` | plain-text body |
| `html` | HTML body |
| `h:<HeaderName>` | custom header, e.g. `h:Reply-To` |
| `attachment` | file; repeatable for multiple attachments |
| `o:tag` | tag; repeatable for multiple tags |

### JSON body

```json
{
  "from": "sender@example.com",
  "to": ["alice@example.com"],
  "cc": ["bob@example.com"],
  "bcc": ["audit@example.com"],
  "subject": "Hello",
  "text": "Plain text body",
  "html": "<p>HTML body</p>",
  "headers": { "Reply-To": "support@example.com" },
  "tags": ["welcome", "onboarding"]
}
```

### Responses

| Status | Meaning |
| --- | --- |
| `202 Accepted` | queued — body: `{ "id": "spl_...", "message": "Queued. Thank you." }` |
| `400 Bad Request` | validation error |
| `401 Unauthorized` | missing or invalid API key |
| `413 Payload Too Large` | message exceeds `api.max_message_bytes` |
| `429 Too Many Requests` | rate limit exceeded |

### Example — multipart with attachment

```bash
curl -X POST http://localhost:8025/api/v1/messages \
  -H "Authorization: Bearer dsp_live_xxxxxxxxxxxxxxxx" \
  -F from="sender@example.com" \
  -F to="alice@example.com" \
  -F subject="Hello" \
  -F text="See the attached report." \
  -F h:Reply-To="support@example.com" \
  -F o:tag="report" \
  -F attachment=@./report.pdf
```

### Example — JSON

```bash
curl -X POST http://localhost:8025/api/v1/messages \
  -H "Authorization: Bearer dsp_live_xxxxxxxxxxxxxxxx" \
  -H "Content-Type: application/json" \
  -d '{
        "from": "sender@example.com",
        "to": ["alice@example.com"],
        "subject": "Hello",
        "html": "<p>Hi there</p>",
        "tags": ["welcome"]
      }'
```

## GET /api/v1/messages/{id}

Return the current status of a previously submitted message.

Status is one of `queued`, `processing`, `delivered`, `retrying`, or `failed`. When available, the response also includes the provider message id, a timestamp, and the delivery duration.

```bash
curl https://localhost:8026/api/v1/messages/spl_xxxxxxxx \
  -H "Authorization: Bearer dsp_live_xxxxxxxxxxxxxxxx"
```

## GET /api/v1/messages

List recent messages submitted with the calling key.

| Query parameter | Notes |
| --- | --- |
| `limit` | number of results, `1`–`200` |
| `status` | filter by `queued`, `processing`, `delivered`, `retrying`, or `failed` |

```bash
curl "https://localhost:8026/api/v1/messages?limit=50&status=failed" \
  -H "Authorization: Bearer dsp_live_xxxxxxxxxxxxxxxx"
```
