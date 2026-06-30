---
title: Postmark
description: Relay mail through Postmark's REST API from Dispatch SMTP Relay.
sidebar:
  order: 5
---

Postmark is an HTTP-based email provider. Add it from the dashboard's **Relays** page, choose **Postmark**, and fill in the settings below. See the [provider overview](/providers/overview/) for how relays fit into routing.

## How it works

Dispatch sends through Postmark's REST API (JSON). All standard message capabilities are supported - see [message features](/sending/message-features/) for the full list (multiple To recipients, CC, BCC, text + HTML bodies, custom headers, and attachments).

## Settings

| Setting        | Required | Description                                              |
| -------------- | -------- | ------------------------------------------------------- |
| Server Token   | Yes      | The Postmark server API token. Encrypted at rest.        |
| Message Stream | No       | The message stream to send on (defaults to `outbound`).  |

## Notes

- The **Server Token** comes from a specific Postmark server's API Tokens tab.
- Set **Message Stream** if you use a non-default stream (e.g. a separate broadcast stream).
- After saving, send a **test email** from the dashboard and watch the relay log live to confirm delivery.
