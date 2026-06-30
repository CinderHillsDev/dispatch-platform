---
title: Bird
description: Relay mail through Bird's REST API from Dispatch SMTP Relay.
sidebar:
  order: 10
---

Bird is an HTTP-based email provider. Add it from the dashboard's **Relays** page, choose **Bird**, and fill in the settings below. See the [provider overview](/providers/overview/) for how relays fit into routing.

## How it works

Dispatch sends through Bird's REST API (JSON). All standard message capabilities are supported - see [message features](/sending/message-features/) for the full list (multiple To recipients, CC, BCC, text + HTML bodies, custom headers, and attachments).

## Settings

| Setting         | Required | Description                                                    |
| --------------- | -------- | ------------------------------------------------------------- |
| API credentials | Yes      | The API credentials from your Bird account. Encrypted at rest. |

## Notes

- Obtain your API credentials from the Bird dashboard.
- Verify your sending domain in Bird before relaying production mail.
- After saving, send a **test email** from the dashboard and watch the relay log live to confirm delivery.
