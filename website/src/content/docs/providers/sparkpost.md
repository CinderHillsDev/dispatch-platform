---
title: SparkPost
description: Relay mail through SparkPost's REST API from Dispatch.
sidebar:
  order: 7
---

SparkPost is an HTTP-based email provider. Add it from the dashboard's **Relays** page, choose **SparkPost**, and fill in the settings below. See the [provider overview](/providers/overview/) for how relays fit into routing.

## How it works

Dispatch sends through SparkPost's REST API (JSON). All standard message capabilities are supported - see [message features](/sending/message-features/) for the full list (multiple To recipients, CC, BCC, text + HTML bodies, custom headers, and attachments).

## Settings

| Setting | Required | Description                                                       |
| ------- | -------- | ---------------------------------------------------------------- |
| API Key | Yes      | Your SparkPost API key. Encrypted at rest.                       |
| Region  | No       | Set for SparkPost EU accounts; leave blank for the default (US). |

## Notes

- Create an API key in the SparkPost dashboard with Transmissions permission.
- If your account is on SparkPost EU, set the **Region** accordingly so requests hit the correct host.
- After saving, send a **test email** from the dashboard and watch the relay log live to confirm delivery.
