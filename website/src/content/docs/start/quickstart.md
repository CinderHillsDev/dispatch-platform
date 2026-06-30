---
title: Quickstart
description: Install Dispatch, point an app at it, and send your first message - whatever way you deploy.
sidebar:
  order: 2
---

Three steps to your first relayed message: install Dispatch, connect a provider, send. The database
schema is created automatically on first start.

## 1. Install Dispatch

Pick whichever fits - full steps in [Deployment](/deployment/overview/):

- **[Virtual appliance](/deployment/appliance/)** - import a prebuilt VM (Hyper-V / VMware / KVM /
  Proxmox) and power on. Nothing to install; SQL + Dispatch are already inside.
- **[Linux](/deployment/linux/)** - `install.sh` sets up a systemd service (and SQL Server Express,
  if you want it).
- **[Windows](/deployment/windows/)** - a single-file installer (Windows Service + SQL + firewall).
- **[Docker](/deployment/docker/)** - handy for trying it locally or on container hosts.

## 2. Open the dashboard

Browse to `https://<host>:8420` and accept the self-signed certificate warning. On first run a short
setup wizard walks you through **setting the admin password**, connecting a provider, and sending a
test.

## 3. Add a relay and test it

Go to **Settings → Relay Provider**, choose your provider, and enter its credentials. Click
**Send Test Email** to verify it works - watch the result appear live in the relay log. See
[Relay providers](/providers/overview/) for per-provider settings.

## 4. Send mail

Point your application at the SMTP listener (ports **25**/**587**) or use the HTTP API:

```bash
# Create an API key first (Settings → API Keys), then:
curl -X POST http://<host>:8025/api/v1/messages \
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
and every message is captured to the [Local Inbox](/sending/local-inbox/) for inspection - a built-in
mail trap for development and CI.
