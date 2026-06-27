---
title: Message features
description: Recipients, CC/BCC, attachments, custom headers, and tags — supported across all providers.
sidebar:
  order: 4
---

Whatever you submit over [SMTP](/sending/smtp/) or the [HTTP API](/sending/http-api/), Dispatch
preserves the full message and relays it faithfully. These features work across **all**
[providers](/providers/overview/).

## Recipients, CC & BCC

Multiple `To`, `Cc`, and `Bcc` recipients are fully supported and parsed independently. BCC
recipients receive the message without appearing in the headers other recipients see. The relay log
records each recipient class separately.

## Attachments

Multiple attachments per message, any MIME type. They're streamed and preserved via MimeKit. The
total message size is bounded by a global and per-relay limit (default 25 MiB); the effective ceiling
is also capped to the active provider's maximum.

## Custom headers

Set arbitrary headers — `h:<Name>` fields (multipart), the `headers` object (JSON), or simply include
them in the raw SMTP message. Raw-MIME providers (Mailgun, Amazon SES, generic SMTP) preserve them
verbatim.

## Tags

Attach searchable **tags** (`o:tag` in multipart, `tags[]` in JSON) as metadata for filtering in the
message log. Tags are Dispatch metadata, not email headers.

## What's recorded

Every message is logged with its recipients, subject, the relay and routing rule used, the provider's
response and message ID, duration, ingest source (SMTP / API / dashboard test), source IP, attachment
count, and more — all searchable and filterable in the dashboard. See
[Health & metrics](/operations/health-metrics/) for aggregate stats.
