# Deploy Dispatch to Azure

One-click deploy of Dispatch (the open-source SMTP relay) onto a single Ubuntu 24.04 VM. The template
provisions the VM + networking and, on first boot, runs the official `install.sh --install-postgres`, which
installs **PostgreSQL** and Dispatch as a systemd service with a self-signed dashboard certificate.

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2FCinderHillsDev%2Fdispatch-platform%2Fmain%2Fdeploy%2Fazure%2Fazuredeploy.json)

## What it creates

A VM plus a VNet, public IP (with a DNS name), NIC, and an NSG opening:

| Port | For |
|---|---|
| 22 | SSH (admin) |
| 8420 | Dashboard (HTTPS) |
| 25, 587 | SMTP submission (apps -> Dispatch) |
| 8025 | HTTP ingestion API |

## Parameters

| Parameter | Default | Notes |
|---|---|---|
| `vmName` | `dispatch` | Names the VM and related resources. |
| `adminUsername` | `azureuser` | Linux SSH admin user. |
| `authenticationType` | `sshPublicKey` | `sshPublicKey` (recommended) or `password`. |
| `adminPasswordOrKey` | - | Your SSH **public key** (or the admin password if you chose `password`). |
| `dispatchAdminPassword` | - | Dashboard admin password - you log in with this at `https://<host>:8420`. |
| `vmSize` | `Standard_B2s` | 2 vCPU / 4 GB is a fine starting point. |
| `dispatchVersion` | `0.5.0` | The [release](https://github.com/CinderHillsDev/dispatch-platform/releases) to install. |

## After deployment

Install runs on first boot and takes a few minutes. Then:

1. Open the **`dashboardUrl`** output (`https://<dns-name>:8420`). It uses a **self-signed certificate**, so
   your browser will warn - that's expected; proceed past it (or configure a real cert later under
   **System -> Settings**).
2. Log in with the **`dispatchAdminPassword`** you set.
3. Add a relay (provider), then point your apps at the VM's FQDN on **25/587** (SMTP) or **8025** (HTTP API).

SSH in with the **`sshCommand`** output to see logs: `journalctl -u dispatch -f`.

## Notes & gotchas

- **Outbound port 25 is blocked on Azure by default** (anti-spam). This does **not** affect Dispatch when you
  relay through a provider's API or a smart host on 587 - which is the normal setup. It only matters if you
  try to deliver directly to recipient mail servers on port 25.
- **Security:** the template opens the SMTP/API ports to the internet, but Dispatch ships with a CIDR
  allow-list defaulting to private ranges, so it is not an open relay out of the box. For production, tighten
  the NSG source ranges (or the in-app allow-list) to just the networks your apps send from, and set up a
  real TLS certificate.
- This is a plain ARM template, **not** an Azure Marketplace listing - no Partner Center account or
  certification required. It simply automates a normal VM install.

## Customizing

The template lives at [`azuredeploy.json`](azuredeploy.json). To deploy from a fork or a pinned commit,
change the `Deploy to Azure` link's encoded URL to point at your raw `azuredeploy.json`, and adjust the
release-download URL in the template's `cloudInit` variable if your fork publishes elsewhere.
