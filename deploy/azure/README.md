# Deploy Dispatch to Azure

One-click deploy of Dispatch (the open-source SMTP relay) onto a single Ubuntu 24.04 VM. The template
provisions the VM + networking and, on first boot, runs the official `install.sh --install-postgres`, which
installs **PostgreSQL** and Dispatch as a systemd service with a self-signed dashboard certificate.

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2FCinderHillsDev%2Fdispatch-platform%2Fmain%2Fdeploy%2Fazure%2Fazuredeploy.json/createUIDefinitionUri/https%3A%2F%2Fraw.githubusercontent.com%2FCinderHillsDev%2Fdispatch-platform%2Fmain%2Fdeploy%2Fazure%2FcreateUiDefinition.json)

The deploy form's **Networking** tab lets you **create a new VNet or pick an existing one** (and its subnet).

## What it creates

A VM plus a public IP (with a DNS name), NIC, and an NSG - and, unless you select an existing VNet, a new VNet. The NSG (attached to the NIC) opens:

| Port | For | Open to (default) |
|---|---|---|
| 22 | SSH (admin) | Internet |
| 8420 | Dashboard (HTTPS) | Internet |
| 25, 587 | SMTP submission (apps -> Dispatch) | **VNet only** |
| 8025 | HTTP ingestion API | **VNet only** |

The mail-submission ports (SMTP + API) are **not exposed to the internet** by default - only resources in
this VM's VNet can reach them. Widen this with the `submissionSource` parameter (a CIDR, or `Internet`) only
if your apps send from outside the VNet and you accept the risk.

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
| `submissionSource` | `VirtualNetwork` | Who may reach the SMTP/API submission ports. VNet-only by default (not internet-exposed); set to a CIDR or `Internet` to widen. |
| `vnetNewOrExisting` | `new` | `new` creates a VNet; `existing` deploys the VM into a VNet you already have. |
| `vnetName` | `<vmName>-vnet` | New VNet's name, or the name of your existing VNet. |
| `vnetResourceGroup` | *(deployment RG)* | Resource group of the existing VNet (only used when `vnetNewOrExisting=existing`). |
| `vnetAddressPrefix` | `10.20.0.0/16` | Address space for a new VNet (ignored for existing). |
| `subnetName` | `default` | Subnet for the VM's NIC. Created for a new VNet; must already exist for an existing one. |
| `subnetAddressPrefix` | `10.20.0.0/24` | Subnet range for a new VNet (ignored for existing). |

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
- **Security:** the mail-submission ports (SMTP 25/587 and API 8025) are restricted to the **VNet** by
  default (`submissionSource`), so they are never exposed to the internet - an internet-facing mail port is a
  prime abuse target. Only widen `submissionSource` (to a CIDR, or `Internet`) if your apps genuinely send
  from outside the VNet. Dispatch also ships with an in-app CIDR allow-list defaulting to private ranges, so
  it is not an open relay even then. For production, also set up a real TLS certificate.
- This is a plain ARM template, **not** an Azure Marketplace listing - no Partner Center account or
  certification required. It simply automates a normal VM install.

## Customizing

The template lives at [`azuredeploy.json`](azuredeploy.json) and the portal form at
[`createUiDefinition.json`](createUiDefinition.json). To deploy from a fork or a pinned commit, change **both**
encoded URLs in the `Deploy to Azure` link (the `uri/...` template and the `createUIDefinitionUri/...` form) to
point at your raw files, and adjust the release-download URL in the template's `cloudInit` variable if your
fork publishes elsewhere.

You can also deploy from the CLI, e.g. into an existing VNet:

```bash
az deployment group create -g <rg> --template-file azuredeploy.json \
  --parameters vnetNewOrExisting=existing vnetName=<my-vnet> vnetResourceGroup=<vnet-rg> subnetName=<my-subnet> \
               adminPasswordOrKey="$(cat ~/.ssh/id_rsa.pub)" dispatchAdminPassword=<pw>
```
