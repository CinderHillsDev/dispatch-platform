---
title: Mailgun
description: Relay mail through Mailgun's HTTP API from Dispatch SMTP Relay.
sidebar:
  order: 2
---

Mailgun is an HTTP-based email provider. Add it from the dashboard's **Relays** page, choose **Mailgun**, and fill in the settings below. See the [provider overview](/providers/overview/) for how relays fit into routing.

## How it works

Dispatch posts the raw MIME message to Mailgun's `/messages.mime` endpoint. Because the original MIME is preserved, all of your headers and attachments pass through exactly as built - see [message features](/sending/message-features/) for the full list (multiple To recipients, CC, BCC, text + HTML bodies, custom headers, and attachments).

## Settings

| Setting   | Required | Description                                        |
| --------- | -------- | -------------------------------------------------- |
| API Key   | Yes      | Your Mailgun private API key. Encrypted at rest.   |
| Domain    | Yes      | The Mailgun sending domain (e.g. `mg.example.com`).|
| Region    | Yes      | `US` or `EU`, matching your Mailgun account region.|

## Notes

- Pick the **Region** that matches where your Mailgun domain is hosted; using the wrong region results in authentication failures.
- After saving, send a **test email** from the dashboard and watch the relay log live to confirm delivery.
