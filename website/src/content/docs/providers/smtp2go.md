---
title: SMTP2GO
description: Relay mail through SMTP2GO's REST API from Dispatch SMTP Relay.
sidebar:
  order: 8
---

SMTP2GO is an HTTP-based email provider. Add it from the dashboard's **Relays** page, choose **SMTP2GO**, and fill in the settings below. See the [provider overview](/providers/overview/) for how relays fit into routing.

## How it works

Dispatch sends through SMTP2GO's REST API (JSON). All standard message capabilities are supported - see [message features](/sending/message-features/) for the full list (multiple To recipients, CC, BCC, text + HTML bodies, custom headers, and attachments).

## Settings

| Setting | Required | Description                                 |
| ------- | -------- | ------------------------------------------- |
| API Key | Yes      | Your SMTP2GO API key. Encrypted at rest.    |

## Notes

- Generate an API key in the SMTP2GO dashboard under Sending → API Keys.
- Verify your sender domain in SMTP2GO before relaying production mail.
- After saving, send a **test email** from the dashboard and watch the relay log live to confirm delivery.
