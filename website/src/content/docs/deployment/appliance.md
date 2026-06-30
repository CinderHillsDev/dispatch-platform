---
title: Virtual appliance
description: Import a ready-to-run Dispatch VM on Hyper-V, VMware, KVM, or Proxmox — and manage logins and a static IP.
sidebar:
  order: 5
---

A prebuilt **Ubuntu 24.04 + SQL Server + Dispatch** VM. Nothing to install — import the image for
your hypervisor, power on, and on first boot it configures SQL and starts Dispatch with unique
per-VM secrets. Grab the files from the
[releases page](https://github.com/chrismuench/Dispatch-SMTP-Relay/releases/latest). Recommended:
**2 vCPU / 4 GB RAM**. It boots on DHCP, but a relay your apps point at should have a **static IP**
(see below). All images are Gen2/UEFI.

| Hypervisor | File | Helper |
|---|---|---|
| **Hyper-V** | `dispatch-appliance-<ver>-x64.vhdx.zip` | `Import-DispatchAppliance.ps1` |
| **VMware** (vSphere/ESXi/Workstation/Fusion) | `dispatch-appliance-<ver>-x64.ova` | native OVF import |
| **KVM/libvirt & Proxmox** | `dispatch-appliance-<ver>-x64.qcow2` | `import-libvirt.sh` / `import-proxmox.sh` |

## Import it

### Hyper-V

Unzip the `.vhdx`, then in **PowerShell as Administrator** (the helper is in the download). Run it with
no networking flags for a **guided menu** — it lists the host's virtual switches to pick from, your
storage volumes (with free space) to choose where the VM lives, an optional **VLAN ID**, and
memory/CPU, then confirms before creating:

```powershell
.\Import-DispatchAppliance.ps1 -VhdxPath .\dispatch-appliance.vhdx
```

For **unattended** import, pass `-SwitchName` (skips the menu) plus any of `-VlanId`, `-VmPath`,
`-MemoryGB`, `-CpuCount`, `-Start`:

```powershell
.\Import-DispatchAppliance.ps1 -VhdxPath .\dispatch-appliance.vhdx -SwitchName "External" -VlanId 20 -VmPath "D:\Hyper-V" -Start
```

Either way it creates a Gen2 VM, sets the **Microsoft UEFI CA** Secure Boot template (required for
Linux), disables Dynamic Memory, attaches the switch (tagging the VLAN if you gave one), and optionally
starts it. Manual route: New VM → Generation 2 → use the existing VHDX → Security → Secure Boot
template = Microsoft UEFI Certificate Authority.

Run it as an elevated **Administrator**, or as a member of the **Hyper-V Administrators** group (which
can manage Hyper-V without elevation) — the script checks for one of these and stops with a clear
message otherwise.

### VMware (vSphere / ESXi / Workstation / Fusion)

vSphere Client → **Deploy OVF Template** → select the `.ova` (or *File → Open* in
Workstation/Fusion). Accept defaults — the descriptor already sets EFI firmware and an Ubuntu 64-bit
guest. CLI: `ovftool dispatch-appliance.ova vi://user@vcenter/Datacenter/host/esxi`.

### KVM / libvirt

```bash
sudo ./import-libvirt.sh dispatch-appliance.qcow2 --start
virsh domifaddr dispatch        # find its IP
```

### Proxmox VE

Copy the `.qcow2` + helper to the Proxmox host and run it **as root** (`qm` requires it — the script
checks), picking an unused VM ID (e.g. 9000):

```bash
./import-proxmox.sh dispatch-appliance.qcow2 9000 \
  --storage local-lvm --bridge vmbr0 --start
```

## Logins (there are two)

| | Username | Password | Change it |
|---|---|---|---|
| **OS** (console / SSH) | `ubuntu` | `dispatch` | forced on first login; later `passwd` |
| **Dashboard** (web UI) | — (single admin) | set on first visit to `:8420` | **System → About → Change password** |

SSH is enabled with password auth (`ssh ubuntu@<vm-ip>`). The dashboard password is stored only as a
bcrypt hash.

## Set a static IP (recommended)

It boots on DHCP, but a relay your apps point at should have a **fixed address**. The appliance ships
a helper that auto-detects the NIC, writes netplan, and applies it — log in (console or SSH) and run:

```bash
# interactive — just answer the prompts:
sudo dispatch-set-ip

# or in one line (DNS defaults to 1.1.1.1,8.8.8.8):
sudo dispatch-set-ip -a 192.168.1.50/24 -g 192.168.1.1 -d 1.1.1.1,8.8.8.8

# revert to automatic:
sudo dispatch-set-ip --dhcp
```

It prints the new address; the dashboard is then at `https://192.168.1.50:8420`. Pass `-i <nic>` to
target a specific interface, or `-h` for all options.

## After it boots

First boot takes a couple of minutes (it configures SQL Server). Then browse to
`https://<vm-ip>:8420`, accept the self-signed certificate warning, and **set the admin password** on
the first-run screen. Then add a [relay](/providers/overview/) and a [routing rule](/routing/).
