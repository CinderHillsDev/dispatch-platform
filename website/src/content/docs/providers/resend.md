---
title: Resend
description: Relay mail through Resend's REST API from Dispatch.
sidebar:
  order: 6
---

Resend is an HTTP-based email provider. Add it from the dashboard's **Relays** page, choose **Resend**, and fill in the settings below. See the [provider overview](/providers/overview/) for how relays fit into routing.

## How it works

Dispatch sends through Resend's REST API (JSON). All standard message capabilities are supported - see [message features](/sending/message-features/) for the full list (multiple To recipients, CC, BCC, text + HTML bodies, custom headers, and attachments).

## Settings

| Setting | Required | Description                                  |
| ------- | -------- | -------------------------------------------- |
| API Key | Yes      | Your Resend API key. Encrypted at rest.      |

## Notes

- Create an API key in the Resend dashboard with send permission.
- Verify your sending domain in Resend before relaying production mail.
- After saving, send a **test email** from the dashboard and watch the relay log live to confirm delivery.
