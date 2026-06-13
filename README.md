<div align="center">

# Dispatch SMTP Relay

**Open-source .NET SMTP relay — forward mail from your apps to any cloud provider**

[![Build](https://github.com/chrismuench/Dispatch-SMTP-Relay/actions/workflows/build.yml/badge.svg)](https://github.com/chrismuench/Dispatch-SMTP-Relay/actions)
[![License: AGPL v3 + Commons Clause](https://img.shields.io/badge/License-AGPL_v3_%2B_Commons_Clause-blue.svg)](LICENSE)
[![Latest Release](https://img.shields.io/github/v/release/chrismuench/Dispatch-SMTP-Relay)](https://github.com/chrismuench/Dispatch-SMTP-Relay/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-lightgrey)](#installation)

Point your applications and devices at Dispatch on port 25 or 587. Dispatch queues every message durably and forwards it to Mailgun, SendGrid, Azure Communication Services, or any SMTP smart host — with a live web dashboard to monitor, configure, and troubleshoot everything.

![Dispatch Dashboard Screenshot](docs/images/dashboard.png)

</div>

---

## Why Dispatch?

Most applications need to send email. Wiring every app directly to a cloud provider means scattered credentials, no central log, and no fallback when a provider has an outage. Dispatch sits in the middle:

```
Your apps / devices  →  Dispatch SMTP (port 25/587)   ─┐
                                                         ├→  Mailgun / SendGrid / Azure / SMTP
Your apps / scripts  →  Dispatch API  (port 8421)      ─┘
         ↑                       ↕
    202 / 250 OK instantly   spool directory
    (before any DB or        (durable in-flight queue)
     network call)                  ↕
                              relay_log in SQL
                              (after-the-fact history)
                                    ↕
                             Web UI (port 8420)
                         Configure · Monitor · Debug
```

- **`250 OK` before anything else** — Dispatch writes the message to a local spool file and acknowledges the sender immediately. No database, no HTTP call, just a file write. SQL Server is only written to *after* the provider accepts the message
- **Spool directory is the queue** — `.eml` files survive restarts, crashes, and SQL outages. If SQL Server is down, mail still flows
- **One place to manage credentials** — rotate an API key once, not in every app
- **Full message log** — after-the-fact history in SQL Server, searchable in the UI
- **Test before you commit** — verify provider credentials with a live relay log before saving

---

## Features

| | |
|---|---|
| 📨 **SMTP listener** | Accepts on ports 25 and 587; STARTTLS, AUTH; app-layer CIDR allow-list; denied connections logged |
| 🌐 **HTTP ingestion API** | `POST /api/v1/messages` on port 8421; multipart or JSON; API key auth; Mailgun-compatible |
| ⚡ **Instant 250 OK** | Message written to spool directory before acknowledging sender — no DB or network on the hot path |
| 📁 **Spool queue** | Local `.eml` files are the durable queue; survive crashes, restarts, and SQL outages |
| 🔀 **Relay routing** | Named relay configs; domain/subdomain routing rules; default relay catch-all; simulate tool |
| ☁️ **Provider support** | Mailgun, SendGrid, Azure Communication Services, generic SMTP, dev/none mode |
| 🔄 **Auto-retry** | Exponential back-off (30 s → 5 min → 30 min); failed messages in `spool/failed/` with retry-from-UI |
| 🖥️ **Web UI** | Embedded React dashboard; no separate web server needed |
| 📊 **Message log** | After-the-fact history in SQL Server; searchable, filterable; CSV export |
| 🧪 **Provider testing** | Send a real test email from the settings page; watch the relay log live |
| 🗑️ **Auto-purge** | Time-based retention + size-based pressure purge (triggers at 9.5 GB, target 9.0 GB) |
| 🔒 **Security** | Encrypted credential storage; required admin password (set at install); localhost-first defaults |
| 🪟 **Windows** | Installs as a Windows Service; MSI with firewall rules; GUI bootstrap wizard |
| 🐧 **Linux** | Runs as a systemd unit; interactive bash installer |
| ⬆️ **Upgrades** | Spool drain → schema migration → binary swap; rollback on failure |

---

## Installation

### Windows

Download and run `DispatchSetup-{version}-x64.exe` from the [latest release](https://github.com/chrismuench/Dispatch-SMTP-Relay/releases/latest).

The setup wizard:
1. Detects any existing SQL Server instance on your machine
2. Offers to install SQL Server Express silently if none is found, or lets you connect to an existing instance
3. Creates the `DispatchQueue` database and applies the schema
4. Installs Dispatch as a Windows Service and opens the dashboard in your browser

**Silent install** (SQL Server already present):
```bat
DispatchSetup-1.0.0-x64.exe --silent --server "localhost\SQLEXPRESS" --auth windows
```

### Linux

```bash
curl -sSL https://github.com/chrismuench/Dispatch-SMTP-Relay/releases/latest/download/install.sh | sudo bash
```

The script detects or installs SQL Server Express, creates the database, installs the systemd service, and prints the dashboard URL when done.

**Check the service:**
```bash
sudo systemctl status dispatch
sudo journalctl -u dispatch -f
```

---

## Quick Start

1. Open the dashboard at **https://localhost:8420** (accept the self-signed certificate warning on first visit, or replace the cert with one from your internal CA)
2. Go to **Settings → Relay Provider** and enter your provider credentials
3. Click **Send Test Email** to verify the credentials work — watch the live relay log
4. Click **Save Settings**
5. Point your application at `localhost:25` or `localhost:587` and send mail

That's it. Dispatch handles everything from there.

### Sending via HTTP API

If you prefer HTTP over SMTP, use the ingestion API on port 8421:

```bash
# Create an API key in the web UI first (Settings → API Keys), then:
curl -X POST http://localhost:8421/api/v1/messages \
  -H "Authorization: Bearer dsp_live_your_key_here" \
  -F from="App <noreply@myapp.com>" \
  -F to="user@example.com" \
  -F subject="Hello" \
  -F html="<p>Hello world</p>" \
  -F text="Hello world"
# → 202 Accepted: { "id": "spl_a1b2c3d4", "message": "Queued. Thank you." }
```

Supports `multipart/form-data` (with file attachments) and `application/json`. The API is intentionally similar to Mailgun's `/messages` endpoint.

---

## Configuration

All settings are managed through the web UI and stored in SQL Server. The only thing in `appsettings.json` is the database connection string — there is nothing else to edit manually.

### Getting to the UI

Open **http://localhost:8420** after installation. On first run, all settings have sensible defaults. The only required step is entering your relay provider credentials under **Settings → Relay Provider**.

### SMTP Listener

| Setting | Default | Notes |
|---|---|---|
| Ports | 25, 587 | Comma-separated list |
| Bind address | 0.0.0.0 | Listens on all interfaces |
| Allowed IPs / CIDRs | `127.0.0.1/32` | Add your subnet, e.g. `192.168.1.0/24`; denied connections are logged |
| Require AUTH | false | Enable to require username/password from senders |
| Max message size | 25 MB | Capped to active provider limit automatically |
| TLS certificate | — | Path to PFX file for STARTTLS support |

### Relay Providers

| Provider | Required settings |
|---|---|
| **Mailgun** | API Key, Domain, Region (US/EU) |
| **SendGrid** | API Key |
| **Azure Communication Services** | Connection String, Sender Address |
| **SMTP (generic)** | Host, Port, Username, Password, TLS mode |
| **Local (developer mode)** | — captures mail to a local inbox; never delivers externally |

### Retention

Delivered log entries are purged after **30 days** by default. Failed entries are kept for **90 days**. Spool files in `failed/` are purged after **30 days**.

Dispatch also monitors database size and automatically purges the oldest log rows when the database approaches **9.5 GB** — keeping it safely below the SQL Server Express 10 GB limit.

All retention periods and size thresholds are configurable under **Settings → Storage & Retention**.

---

## Supported Providers

| Provider | Mechanism |
|---|---|
| [Mailgun](https://mailgun.com) | REST API |
| [SendGrid](https://sendgrid.com) | Web API v3 (official SDK) |
| [Azure Communication Services](https://azure.microsoft.com/en-us/products/communication-services) | SDK |
| Any SMTP smart host | SMTP via MailKit (AWS SES, Office 365, Postfix, …) |

More providers planned — see [Appendix A of the spec](docs/SPEC.md) and [open issues](https://github.com/chrismuench/Dispatch-SMTP-Relay/issues?q=label%3Aprovider).

---

## Screenshots

<details>
<summary>Dashboard</summary>

![Dashboard](docs/images/dashboard.png)

</details>

<details>
<summary>Message Log</summary>

![Message Log](docs/images/message-log.png)

</details>

<details>
<summary>Provider Settings with live test log</summary>

![Provider Settings](docs/images/provider-settings.png)

</details>

<details>
<summary>Storage & Retention</summary>

![Storage and Retention](docs/images/retention.png)

</details>

---

## Requirements

### Windows
- Windows 10 / Windows Server 2019 or later (x64)
- .NET 10 runtime (bundled in the installer)
- SQL Server Express 2019+ or any SQL Server edition (installer can download and set up Express for you)

### Linux
- Ubuntu 20.04+ / Debian 11+ / RHEL 8+ (x64)
- SQL Server 2019+ for Linux (installer can set it up)

---

## Building from Source

```bash
# Clone
git clone https://github.com/chrismuench/Dispatch-SMTP-Relay.git
cd dispatch

# Build the React UI
cd src/Dispatch.UI
npm install
npm run build
cd ../..

# Build and run
dotnet run --project src/Dispatch.Service

# Run tests
dotnet test
```

The web UI is served at `http://localhost:8420` and the SMTP listener starts on ports 25 and 587.

> **Note:** You need SQL Server Express (or any SQL Server instance) running locally and a connection string in `src/Dispatch.Service/appsettings.Development.json` before the service will start.

---

## Project Structure

```
dispatch/
  src/
    Dispatch.Core/         # SMTP listener, relay pipeline, queue, models, purge
    Dispatch.Web/          # ASP.NET Core host, REST API, SignalR hub
    Dispatch.UI/           # React + Vite SPA (embedded in Dispatch.Web at build time)
    Dispatch.Providers/    # Mailgun, SendGrid, Azure, SMTP provider implementations
    Dispatch.Service/      # Entry point and DI wiring
  installer/
    windows/            # WiX v5 MSI source
    linux/              # systemd unit template and install.sh
  tests/
    Dispatch.Core.Tests/
    Dispatch.Web.Tests/
    Dispatch.Integration.Tests/
  docs/
    SPEC.md             # Full technical specification
```

---

## Upgrading

Run the new `DispatchSetup-{version}-x64.exe` (Windows) or re-run `install.sh` (Linux). The installer:

1. Detects the existing version automatically
2. Drains the in-flight queue (waits up to 60 s for processing messages to complete)
3. Stops the service
4. Applies any new database schema migrations (additive only — no data loss)
5. Replaces the binary
6. Restarts the service

Configuration and all message history are preserved across upgrades.

---

## Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a PR.

**Good first issues:** provider implementations, UI improvements, documentation. See the [issue tracker](https://github.com/chrismuench/Dispatch-SMTP-Relay/issues?q=label%3A%22good+first+issue%22).

**Adding a provider:** implement `IRelayProvider` in `Dispatch.Providers`, add the settings model, add the UI fields in the provider settings page, add tests. See `SendGridProvider.cs` as the reference implementation.

---

## Security

The admin web UI is **HTTPS-only** — the bootstrap generates a self-signed certificate if you don't have one. The HTTP ingestion API supports both HTTP and HTTPS, since many internal devices (printers, scanners, legacy apps) can't do HTTPS. All listeners enforce access via configurable IP/CIDR allow-lists at the application layer; denied connections are logged with the source IP. Credentials are stored AES-256 encrypted (API keys, provider secrets) or bcrypt-hashed (SMTP sender passwords) in SQL Server. If you find a security issue please report it privately via [GitHub Security Advisories](https://github.com/chrismuench/Dispatch-SMTP-Relay/security/advisories/new) rather than a public issue.

---

## Licence

AGPL-3.0 with Commons Clause — see [LICENSE](LICENSE).

**What you can do:** use Dispatch internally, self-host it, modify it, contribute back.

**What you cannot do:** sell Dispatch, charge for access to a hosted version of it, or distribute it as part of a paid product.

The Commons Clause prevents anyone from taking this code and charging money for the binary or a hosted service. See the [licence FAQ](docs/licence-faq.md) for common scenarios.
