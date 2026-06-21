<div align="center">

# Dispatch SMTP Relay

**Open-source .NET SMTP relay — forward mail from your apps to any cloud provider**

[![Build](https://github.com/chrismuench/Dispatch-SMTP-Relay/actions/workflows/build.yml/badge.svg)](https://github.com/chrismuench/Dispatch-SMTP-Relay/actions)
[![License: AGPL v3 + Commons Clause](https://img.shields.io/badge/License-AGPL_v3_%2B_Commons_Clause-blue.svg)](LICENSE)
[![Latest Release](https://img.shields.io/github/v/release/chrismuench/Dispatch-SMTP-Relay)](https://github.com/chrismuench/Dispatch-SMTP-Relay/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-lightgrey)](#installation)

Point your applications and devices at Dispatch over SMTP (port 2525 by default; 25/587 for production). Dispatch queues every message durably and forwards it to Mailgun, SendGrid, Amazon SES, Postmark, Azure Communication Services, or any SMTP smart host — with a live web dashboard to monitor, configure, and troubleshoot everything.

![Dispatch Dashboard Screenshot](docs/images/dashboard.png)

</div>

---

## Why Dispatch?

Most applications need to send email. Wiring every app directly to a cloud provider means scattered credentials, no central log, and no fallback when a provider has an outage. Dispatch sits in the middle:

```
Your apps / devices  →  Dispatch SMTP (port 25/587)   ─┐
                                                         ├→  Mailgun / SendGrid / Azure / SMTP
Your apps / scripts  →  Dispatch API  (port 8025)      ─┘
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
| 📨 **SMTP listener** | Configurable ports (2525 by default; set 25/587 for production); STARTTLS, AUTH; app-layer CIDR allow-list; denied connections logged |
| 🌐 **HTTP ingestion API** | `POST /api/v1/messages` on port 8025; multipart or JSON; API key auth; Mailgun-compatible |
| ⚡ **Instant 250 OK** | Message written to spool directory before acknowledging sender — no DB or network on the hot path |
| 📁 **Spool queue** | Local `.eml` files are the durable queue; survive crashes, restarts, and SQL outages |
| 🔀 **Relay routing** | Named relay configs; recipient/sender routing rules; default relay catch-all; simulate tool |
| ☁️ **Provider support** | Mailgun, SendGrid, Amazon SES, Postmark, Resend, SparkPost, SMTP2GO, Azure Communication Services, generic SMTP, plus Local (developer capture) mode |
| 🔄 **Auto-retry** | Exponential back-off (30 s → 5 min → 30 min); failed messages in `spool/failed/` with retry-from-UI |
| 🖥️ **Web UI** | Embedded React dashboard; no separate web server needed |
| 📊 **Message log** | After-the-fact history in SQL Server; searchable and filterable |
| 🧪 **Provider testing** | Send a real test email from the settings page; watch the relay log live |
| 🗑️ **Auto-purge** | Time-based retention + size-based pressure purge (triggers at 9.5 GB, target 9.0 GB) |
| 🔒 **Security** | Encrypted credential storage; required admin password (set at install); dashboard/API gated by auth, SMTP limited to private ranges by default |
| 🪟 **Windows** | Installs as a Windows Service; MSI + bundled SQL Express bootstrapper with firewall rules |
| 🐧 **Linux** | Runs as a systemd unit; interactive bash installer |
| ⬆️ **Upgrades** | In-place MSI/major upgrade with additive schema migrations; config + history preserved (manual queue drain available) |

---

## Installation

### Windows

Download and run `DispatchSetup-{version}-x64.exe` from the [latest release](https://github.com/chrismuench/Dispatch-SMTP-Relay/releases/latest). It's a single bundled installer that:

1. Installs **SQL Server 2025 Express** (a dedicated `DISPATCHSQL` instance) — skipped automatically if that instance already exists
2. Installs the Dispatch MSI; the `DispatchLog` database and schema are created on first service start
3. Installs and starts Dispatch as a Windows Service

**Silent install:**
```bat
DispatchSetup-1.0.0-x64.exe /quiet
```

**Against an existing SQL Server** (skip the bundled SQL Express — install the MSI directly and point it at your server):
```bat
msiexec /i Dispatch-1.0.0-x64.msi /qn SQLCONN="Server=YOURHOST;Database=DispatchLog;User Id=sa;Password=***;TrustServerCertificate=True;Encrypt=True"
```

### Linux

Download `dispatch-{version}-linux.tar.gz` from the [latest release](https://github.com/chrismuench/Dispatch-SMTP-Relay/releases/latest) — one universal, self-contained bundle (x64 + arm64, no .NET SDK needed). `install.sh` auto-detects your CPU architecture.

```bash
tar xzf dispatch-*-linux.tar.gz && cd dispatch-*-linux

# Install with a bundled database (SQL Server 2025 Express on x64; Azure SQL Edge container on arm64):
sudo ./install.sh --install-sql \
  --sa-password '<StrongSaPassw0rd!>' --admin-password '<DashboardPassword!>'

# ...or against an existing SQL Server / Azure SQL:
sudo ./install.sh \
  --sql-connection 'Server=YOURHOST;Database=DispatchLog;User Id=sa;Password=***;TrustServerCertificate=True;Encrypt=True' \
  --admin-password '<DashboardPassword!>'
```

The script lays out `/opt/dispatch`, writes config, installs the systemd unit, and starts the service.
SQL Server has no arm64 Linux build, so on arm64 `--install-sql` uses an **Azure SQL Edge** container (needs Docker); or point `--sql-connection` at an external instance.

**Check the service:**
```bash
sudo systemctl status dispatch
sudo journalctl -u dispatch -f
```

### Docker

The container image is multi-arch (`linux/amd64` + `linux/arm64`), so the same tag runs natively on
x86 servers and Apple Silicon.

**Try it locally** — one command brings up Dispatch + SQL together (Azure SQL Edge, arm64-native):
```bash
docker compose up --build      # from a clone of this repo
# dashboard → https://localhost:8420  (self-signed cert; default login password: see docker-compose.yml)
```

**Run from GHCR** against your own SQL Server / Azure SQL:
```bash
docker run -d --name dispatch \
  -p 8420:8420 -p 8025:8025 -p 2525:2525 \
  -e ConnectionStrings__DispatchLog="Server=<host>,1433;Database=DispatchLog;User Id=sa;Password=<pw>;TrustServerCertificate=True;Encrypt=True" \
  -e AdminPassword="<DashboardPassword!>" \
  -v dispatch-spool:/app/.dispatch-spool \
  ghcr.io/chrismuench/dispatch-smtp-relay:latest
```

Only two settings are passed in (spec §12.1): the SQL connection string and the first-run admin
password. Everything else is seeded into the SQL config table and managed in the dashboard. The schema
is created/migrated automatically on first start. The default source-IP allow-lists are container-aware
(dashboard + API allow all and are gated by the password / API keys; the SMTP listener accepts loopback
and private/RFC1918 ranges so it isn't an open relay) — tighten them in **Settings**.

---

## Quick Start

1. Open the dashboard at **https://localhost:8420** and log in with the admin password you set at install. The dashboard is HTTPS-only; with no cert configured it uses an auto-generated **self-signed** certificate, so accept the browser warning (or configure your own cert — see [Configuration](#configuration))
2. Go to **Settings → Relay Provider** and enter your provider credentials
3. Click **Send Test Email** to verify the credentials work — watch the live relay log
4. Click **Save Settings**
5. Point your application at `localhost:2525` and send mail (the default port; set `25`/`587` in **Settings → SMTP Listener** for production)

That's it. Dispatch handles everything from there.

### Sending via HTTP API

If you prefer HTTP over SMTP, use the ingestion API on port 8025:

```bash
# Create an API key in the web UI first (Settings → API Keys), then:
curl -X POST http://localhost:8025/api/v1/messages \
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

All settings are managed through the web UI and stored in SQL Server. The only things in `appsettings.json` are the database connection string and (optionally) the Web UI TLS certificate (spec §12.1) — everything else lives in the SQL config table, seeded with sensible defaults on first run.

### Getting to the UI

Open **https://localhost:8420** after installation (HTTPS-only; accept the self-signed cert warning if you haven't configured your own). On first run a short **setup wizard** walks you through connecting a provider, sending a test, and any routing rules.

### Forgot the admin password?

Run the built-in reset command **on the server** (it writes a new bcrypt hash to the database):

```bash
# Linux (run as the service account so it reads the same config):
sudo -u dispatch /opt/dispatch/Dispatch.Service reset-admin-password
# Windows (from the install dir):    Dispatch.Service.exe reset-admin-password
# Docker:                            docker exec -it dispatch ./Dispatch.Service reset-admin-password
```

It prompts for a new password (enforcing the same policy) and exits — no service downtime.

### SMTP Listener

| Setting | Default | Notes |
|---|---|---|
| Ports | 2525 | Comma-separated list; set `25,587` for production (require elevation) |
| Bind address | 0.0.0.0 | Listens on all interfaces |
| Allowed IPs / CIDRs | loopback + private ranges | `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16` (so it isn't an open relay); add your subnet, clear to allow all; denied connections are logged |
| Require AUTH | false | Enable to require username/password from senders |
| Max message size | 0 (no limit) | Effective limit is auto-capped to the active provider's maximum |
| TLS certificate | — | Path to PFX file for STARTTLS support |

### Relay Providers

| Provider | Required settings |
|---|---|
| **Mailgun** | API Key, Domain, Region (US/EU) |
| **SendGrid** | API Key |
| **Amazon SES** | Access Key ID, Secret Access Key, Region |
| **Postmark** | Server Token (Message Stream optional) |
| **Resend** | API Key |
| **SparkPost** | API Key (Region optional) |
| **SMTP2GO** | API Key |
| **Azure Communication Services** | Connection String, Sender Address |
| **SMTP (generic)** | Host, Port, Username, Password, TLS mode |
| **Local (developer mode)** | — captures mail to the spool; never delivers externally |

### Retention & storage safety

A purge runs every **6 hours** (and on demand via **Settings → Storage maintenance** or `POST /api/purge/run`). By default it removes:

- **Delivered** log rows after **30 days**; **failed** after **90 days**
- Spool files in `failed/` after **30 days**; captured (Local mode) files after **7 days**

Dispatch also watches the database file size and purges the oldest log rows once it reaches **9.5 GB**, down to **9.0 GB** — keeping it safely below the SQL Server Express 10 GB limit.

These retention periods and the size thresholds are editable under **Settings → Retention** (the 6-hour schedule and the retrying/test-message retention are fixed defaults). Separately, Dispatch monitors free disk on the spool volume: as it runs low it throttles, then temporarily refuses new SMTP intake with a transient `4xx` (senders retry) until space recovers — so a full disk never corrupts the spool.

---

## Supported Providers

| Provider | Mechanism |
|---|---|
| [Mailgun](https://mailgun.com) | REST API |
| [SendGrid](https://sendgrid.com) | Web API v3 (official SDK) |
| [Amazon SES](https://aws.amazon.com/ses/) | SES v2 API (official SDK, raw MIME) |
| [Postmark](https://postmarkapp.com) | REST API |
| [Resend](https://resend.com) | REST API |
| [SparkPost](https://www.sparkpost.com) | REST API |
| [SMTP2GO](https://www.smtp2go.com) | REST API |
| [Azure Communication Services](https://azure.microsoft.com/en-us/products/communication-services) | SDK |
| Any SMTP smart host | SMTP via MailKit (Office 365, Postfix, …) |

More providers welcome — see the [provider issues](https://github.com/chrismuench/Dispatch-SMTP-Relay/issues?q=label%3Aprovider) and the **Adding a provider** note under [Contributing](#contributing).

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

---

## Requirements

### Windows
- Windows 10 / Windows Server 2019 or later (x64)
- .NET 10 runtime (bundled in the installer)
- SQL Server (the bundled installer sets up **SQL Server 2025 Express** for you; or point Dispatch at any existing SQL Server 2019+ / Azure SQL)

### Linux
- Ubuntu / Debian or RHEL / Fedora (x64) — `--install-sql` targets SQL Server 2025 (Ubuntu 24.04+ for the bundled SQL)
- Or any existing SQL Server 2019+ / Azure SQL via `--sql-connection`
- arm64: use the [Docker image](#docker) or an external SQL Server (SQL Server has no arm64 Linux build)

---

## Building & running locally

Prerequisites: the **.NET 10 SDK**, **Node.js 20+**, and **Docker** (for SQL).

```bash
git clone https://github.com/chrismuench/Dispatch-SMTP-Relay.git
cd Dispatch-SMTP-Relay

# 1. Start SQL (Azure SQL Edge, arm64-native) — schema is created/migrated on first run
docker compose up -d

# 2. Build the React dashboard and embed it into Dispatch.Web
cd src/Dispatch.UI && npm install && npm run build && cd ../..
rm -rf src/Dispatch.Web/wwwroot && mkdir -p src/Dispatch.Web/wwwroot
cp -r src/Dispatch.UI/dist/* src/Dispatch.Web/wwwroot/

# 3. Run the service (Development reads src/Dispatch.Service/appsettings.Development.json)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Dispatch.Service
```

Ports (defaults): **dashboard `https://localhost:8420`** (HTTPS-only, self-signed by default), **HTTP ingestion API `8025`**, **SMTP `2525`** in
dev (25/587 in production, which require elevated privileges). The dashboard requires an admin password —
set `AdminPassword` in `appsettings.Development.json`, or use the one-time first-run setup screen.

`appsettings.Development.json` (git-ignored) needs at least the SQL connection string:

```json
{
  "ConnectionStrings": { "DispatchLog": "Server=localhost,1433;Database=DispatchLog;User Id=sa;Password=Dispatch_Dev_Pass123;TrustServerCertificate=True;Encrypt=True" },
  "AdminPassword": "change-me-please"
}
```

### Tests

```bash
# Unit/Web tests run without SQL. Data integration tests run when DISPATCH_TEST_SQL is set
# (auto-skipped otherwise):
DISPATCH_TEST_SQL="Server=localhost,1433;User Id=sa;Password=Dispatch_Dev_Pass123;TrustServerCertificate=True;Encrypt=True" \
  dotnet test
```

For production installs see [`installer/`](installer/README.md) (Linux systemd + Windows).

---

## Project Structure

```
Dispatch-SMTP-Relay/
  src/
    Dispatch.Core/         # SMTP listener store, spool pipeline, worker pool, routing, purge, models
    Dispatch.Providers/    # Local, generic SMTP, Mailgun, SendGrid, Amazon SES, Postmark, Resend,
                           #   SparkPost, SMTP2GO, Azure provider implementations
    Dispatch.Data/         # SQL schema/migrations, Dapper repositories, encryption
    Dispatch.Web/          # REST API, SignalR hub, ingestion API, auth, embedded React UI
    Dispatch.UI/           # React + Vite SPA (built, then embedded into Dispatch.Web)
    Dispatch.Service/      # Entry point: WebApplication host wiring SMTP + workers + web in one process
  installer/
    linux/                 # systemd unit + install.sh
    windows/               # install.ps1, WiX MSI (Dispatch.wxs) + Burn bundle, SQL Express bootstrap
  Dockerfile               # multi-arch container image; docker-compose.yml runs Dispatch + SQL
  tests/
    Dispatch.Core.Tests/        # spool/worker/routing/counters (no DB)
    Dispatch.Providers.Tests/   # provider + factory (no DB)
    Dispatch.Web.Tests/         # API/auth via a real-Kestrel fixture (no DB)
    Dispatch.Data.Tests/        # SQL repository integration (auto-skip without DISPATCH_TEST_SQL)
  docs/
    SPEC.md                # Full technical specification
```

---

## Upgrading

To upgrade in place:

- **Windows** — run the new `DispatchSetup-{version}-x64.exe` (or `Dispatch-{version}-x64.msi`). The MSI `MajorUpgrade` stops the service, replaces the binaries, and restarts it.
- **Linux** — download the new tarball and re-run `sudo ./install.sh` (with your existing `--sql-connection`); it republishes `/opt/dispatch` and restarts the service.

Database schema migrations are **additive** and applied automatically on startup, so configuration and all message history are preserved. The spool is durable across the restart.

To finish in-flight messages before upgrading, drain the queue first from **Settings** (or `POST /api/service/drain`, which waits up to 60 s for processing messages to complete). Automatic rollback is not yet implemented — take a database backup before upgrading production.

---

## Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a PR.

**Good first issues:** provider implementations, UI improvements, documentation. See the [issue tracker](https://github.com/chrismuench/Dispatch-SMTP-Relay/issues?q=label%3A%22good+first+issue%22).

**Adding a provider:** implement `IRelayProvider` in `Dispatch.Providers`, add the settings model, add the UI fields in the provider settings page, add tests. See `SendGridProvider.cs` as the reference implementation.

---

## Security

The admin web UI is **HTTPS-only** — it uses your configured TLS certificate (`WebUi:TlsCertPath` in `appsettings.json`) or, if none is set, an auto-generated self-signed certificate persisted across restarts. It never serves plain HTTP. For production, configure a trusted cert (and ideally a reverse proxy). The ingestion API stays HTTP (gated by API keys) for devices that can't do TLS. All listeners enforce access via configurable IP/CIDR allow-lists at the application layer (dashboard and API allow all by default — gated by the admin password and API keys respectively; the SMTP listener is limited to loopback + private ranges so it isn't an open relay); denied connections are logged with the source IP. At rest: API keys and the admin password are **bcrypt-hashed**; provider secrets and SMTP credentials are **encrypted** (AES-256-GCM on Linux/macOS, Windows DPAPI). If you find a security issue please report it privately via [GitHub Security Advisories](https://github.com/chrismuench/Dispatch-SMTP-Relay/security/advisories/new) rather than a public issue.

---

## Licence

AGPL-3.0 with Commons Clause — see [LICENSE](LICENSE).

**What you can do:** use Dispatch internally, self-host it, modify it, contribute back.

**What you cannot do:** sell Dispatch, charge for access to a hosted version of it, or distribute it as part of a paid product.

The Commons Clause prevents anyone from taking this code and charging money for the binary or a hosted service. See the [licence FAQ](docs/licence-faq.md) for common scenarios.
