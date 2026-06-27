---
title: Quickstart
description: Get Dispatch running in a few minutes with Docker, then send your first message.
sidebar:
  order: 2
---

The fastest way to try Dispatch is Docker Compose — it brings up Dispatch **and** its database
together. (For production installs see [Deployment](/deployment/overview/).)

## 1. Start it

```bash
git clone https://github.com/chrismuench/Dispatch-SMTP-Relay.git
cd Dispatch-SMTP-Relay
docker compose up -d --build
```

This starts Dispatch (dashboard `8420`, HTTP API `8025`, SMTP `25` & `587`) plus a local SQL database. The
schema is created automatically on first start.

## 2. Open the dashboard

Browse to **https://localhost:8420** and accept the self-signed certificate warning. On first run a
short setup wizard walks you through setting the admin password, connecting a provider, and sending a
test. (The default Compose login password is in `docker-compose.yml` — change it after logging in.)

## 3. Configure a relay

Go to **Settings → Relay Provider**, pick your provider, and enter its credentials. Click
**Send Test Email** to verify it works — watch the result appear live in the relay log. See
[Relay providers](/providers/overview/) for per-provider settings.

## 4. Send mail

Point your application at `localhost:25` over SMTP, or use the HTTP API:

```bash
# Create an API key first (Settings → API Keys), then:
curl -X POST http://localhost:8025/api/v1/messages \
  -H "Authorization: Bearer dsp_live_your_key_here" \
  -F from="App <noreply@myapp.com>" \
  -F to="user@example.com" \
  -F subject="Hello" \
  -F text="Hello world" \
  -F html="<p>Hello world</p>"
# → 202 Accepted: { "id": "spl_a1b2c3d4", "message": "Queued. Thank you." }
```

## Just testing? Use the Local Inbox

Want to see what your app sends without delivering anything externally? Set the relay to **Local**
and every message is captured to the [Local Inbox](/sending/local-inbox/) for inspection — a built-in
mail trap for development and CI.
