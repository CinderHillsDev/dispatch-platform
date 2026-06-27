---
title: Deployment overview
description: Choose how to run Dispatch — Docker, Linux, Windows, or a ready-to-import virtual appliance.
sidebar:
  order: 1
---

Dispatch runs anywhere .NET 10 and SQL Server reach. Pick the path that fits:

| Method | Best for | Guide |
|---|---|---|
| 🐳 **Docker** | Trying it, or container-native hosts | [Docker](/deployment/docker/) |
| 🐧 **Linux** | A Linux server (systemd) | [Linux](/deployment/linux/) |
| 🪟 **Windows** | A Windows server (service) | [Windows](/deployment/windows/) |
| 📦 **Virtual appliance** | A hypervisor — nothing to install | [Appliance](/deployment/appliance/) |

Every path ends the same way: open `https://<host>:8420`, set the admin password, add a relay. The
SQL schema is created and migrated automatically on first start.

- **Docker, Linux, and Windows** installers can set up SQL Server Express for you, or point at an
  existing SQL Server / Azure SQL.
- The **virtual appliance** is a prebuilt Ubuntu VM with SQL + Dispatch already installed — import,
  power on, done.

:::tip[Recommended]
Dispatch listens on the standard SMTP ports **25** and **587**. Install it on a host with **no other
SMTP software** (Postfix, Sendmail, Exim, …) so those ports are free — otherwise it falls back to
**2525**. See [SMTP listener](/sending/smtp/).
:::

Already running? See [Upgrading](/deployment/upgrading/) for in-place upgrades.
