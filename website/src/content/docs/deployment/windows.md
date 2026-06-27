---
title: Windows
description: Install Dispatch SMTP Relay on Windows with the single bundled installer, including silent and existing-SQL options.
sidebar:
  order: 4
---

Download `DispatchSetup-{version}-x64.exe` from the
[latest release](https://github.com/chrismuench/Dispatch-SMTP-Relay/releases/latest). It's a single
bundled installer that:

1. Installs **SQL Server 2025 Express** (a dedicated `DISPATCHSQL` instance) — skipped automatically if
   that instance already exists.
2. Installs the Dispatch MSI.
3. Installs and starts Dispatch as a **Windows Service**.
4. Opens the firewall ports it needs (**25**, **587**, **8025**, **8420**). The service runs with enough
   privilege to bind the standard SMTP ports; it falls back to **2525** only if 25 is already in use.

The `DispatchLog` database and schema are created on first service start.

## Silent install

```bat
DispatchSetup-1.0.0-x64.exe /quiet
```

## Against an existing SQL Server

The bundle embeds the bare MSI. Extract it with `-layout` and install it directly, pointing at your
server:

```bat
DispatchSetup-1.0.0-x64.exe -layout C:\dispatch-msi
msiexec /i C:\dispatch-msi\Dispatch.msi /qn SQLCONN="Server=YOURHOST;Database=DispatchLog;User Id=sa;Password=***;TrustServerCertificate=True;Encrypt=True"
```

## SmartScreen

CI builds are unsigned, so SmartScreen may warn on first run. Choose **More info → Run anyway** to
proceed.

## After install

Logs are written to `C:\ProgramData\Dispatch\logs`. Open the dashboard at **https://localhost:8420**
(accept the self-signed certificate warning).
