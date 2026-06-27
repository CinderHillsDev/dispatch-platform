---
title: Local Inbox
description: A built-in mail trap — capture and inspect what your app sends without delivering anything.
sidebar:
  order: 3
---

The **Local Inbox** is a built-in mail trap for developers. Point your application at Dispatch with a
relay set to the **Local** provider, and every message is **captured, not delivered** — then inspect
exactly what your app sent, right in the dashboard. No third-party service to wire up, no real inboxes
spammed.

It's ideal for **local development and CI**: verify templates, headers, recipients, and attachments
before you ever send for real.

## Set it up

1. Go to **Relays** and create a relay (or edit one) with the provider set to **Local**. Either make
   it the default relay, or add a [routing rule](/routing/) that sends a test domain (e.g.
   `*@test.local`) to it.
2. Point your app at Dispatch over [SMTP](/sending/smtp/) (`25`/`587`) or the
   [HTTP API](/sending/http-api/) (`8025`), exactly as you would in production.
3. Send mail. Each message is written to the capture spool instead of going to a provider.

## Inspect captured mail

Open **Local Inbox** in the dashboard to see every captured message with:

- Subject, From, and all recipients (**To / Cc / Bcc**)
- The rendered **HTML** body (shown in a sandboxed iframe) and the **plain-text** body
- **Custom headers** and tags
- **Attachments** — listed with type/size and downloadable

## How it works

Captured messages are stored as `.eml` files in the spool's `captured/` directory and surfaced
through the dashboard (and the read-only local API). They never reach a provider. Captured files are
cleaned up by the [retention policy](/configuration/retention-purge/) (7 days by default), so the
inbox doesn't grow forever.

:::tip
Switch a relay between **Local** and a real provider to flip the same routing between "capture for
inspection" and "actually send" without changing your application.
:::
