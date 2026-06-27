---
title: Maileroo
description: Relay mail through Maileroo's REST API from Dispatch SMTP Relay.
sidebar:
  order: 9
---

Maileroo is an HTTP-based email provider. Add it from the dashboard's **Relays** page, choose **Maileroo**, and fill in the settings below. See the [provider overview](/providers/overview/) for how relays fit into routing.

## How it works

Dispatch sends through Maileroo's REST API (JSON). All standard message capabilities are supported — see [message features](/sending/message-features/) for the full list (multiple To recipients, CC, BCC, text + HTML bodies, custom headers, and attachments).

## Settings

| Setting     | Required | Description                                                          |
| ----------- | -------- | ------------------------------------------------------------------- |
| Sending Key | Yes      | Your Maileroo API key. Maileroo calls this a **Sending Key**. Encrypted at rest. |

## Notes

- Find your **Sending Key** in the Maileroo dashboard — it is Maileroo's name for the API key.
- Verify your sending domain in Maileroo before relaying production mail.
- After saving, send a **test email** from the dashboard and watch the relay log live to confirm delivery.
