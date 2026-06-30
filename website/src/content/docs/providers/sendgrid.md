---
title: SendGrid
description: Relay mail through SendGrid's Web API v3 from Dispatch SMTP Relay.
sidebar:
  order: 3
---

SendGrid is an HTTP-based email provider. Add it from the dashboard's **Relays** page, choose **SendGrid**, and fill in the settings below. See the [provider overview](/providers/overview/) for how relays fit into routing.

## How it works

Dispatch sends through SendGrid's Web API v3 using the official SendGrid SDK. All standard message capabilities are supported - see [message features](/sending/message-features/) for the full list (multiple To recipients, CC, BCC, text + HTML bodies, custom headers, and attachments).

## Settings

| Setting | Required | Description                                          |
| ------- | -------- | ---------------------------------------------------- |
| API Key | Yes      | A SendGrid API key with Mail Send permission. Encrypted at rest. |

## Notes

- Create a dedicated API key in the SendGrid dashboard scoped to **Mail Send**.
- Make sure your sending domain or address is verified in SendGrid before relaying.
- After saving, send a **test email** from the dashboard and watch the relay log live to confirm delivery.
