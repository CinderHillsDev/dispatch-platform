# Dispatch SMTP Relay - virtual appliance

A ready-to-run **Ubuntu 24.04 LTS** virtual machine with Dispatch and SQL Server Express pre-installed. Import it, power it on, and the dashboard comes up - no .NET, SQL, or command line needed.

Each release ships the same image in three formats (plus one-command import helpers):

| Hypervisor | File | Helper |
|---|---|---|
| **Hyper-V** | `dispatch-appliance-<ver>-x64.vhdx.zip` | `Import-DispatchAppliance.ps1` |
| **VMware** (vSphere/ESXi/Workstation/Fusion) | `dispatch-appliance-<ver>-x64.ova` | native OVF import |
| **KVM/libvirt & Proxmox** | `dispatch-appliance-<ver>-x64.qcow2` | `import-libvirt.sh` / `import-proxmox.sh` |

All are **Gen2/UEFI**, ~4 GB RAM recommended (SQL Server needs ~2 GB). They boot on DHCP, but a relay your apps point at should have a **static IP** - set one after first boot (see [Static IP](#static-ip)).

## Hyper-V

**Guided menu** (elevated **Administrator**, or a member of the **Hyper-V Administrators** group - the script checks for one), after unzipping the `.vhdx`:
```powershell
.\Import-DispatchAppliance.ps1
```
Run with no networking flags, it walks you through it: it lists the host's **virtual switches** to pick from, your **storage volumes** (with free space) to choose where the VM lives, an optional **VLAN ID**, and memory/CPU - then confirms before creating.

**Unattended** (pass `-SwitchName` to skip the menu):
```powershell
.\Import-DispatchAppliance.ps1 -SwitchName "External" -VlanId 20 -VmPath "D:\Hyper-V" -MemoryGB 6 -Start
```
Either way it creates a Gen2 VM, sets the **Microsoft UEFI CA** Secure Boot template (required for Linux), disables Dynamic Memory, attaches the switch (tagging the VLAN if given), and optionally starts it.

**Or manually:** Hyper-V Manager → New → VM → **Generation 2**, 4096 MB, a DHCP switch (e.g. *Default Switch*), **Use an existing virtual hard disk** → the unzipped `.vhdx`; then Settings → Security → Secure Boot → template **Microsoft UEFI Certificate Authority**.

## VMware (vSphere / ESXi / Workstation / Fusion)

Deploy the OVA: vSphere Client → **Deploy OVF Template** → select `dispatch-appliance-<ver>-x64.ova` (or `File → Open` in Workstation/Fusion). The descriptor sets EFI firmware and a 64-bit Ubuntu guest; accept the defaults. (You can switch the SCSI/NIC to PVSCSI/VMXNET3 after import for performance - the image has the in-kernel drivers.)

## KVM / libvirt

```bash
sudo ./import-libvirt.sh dispatch-appliance.qcow2 --start
```
Creates a UEFI VM via `virt-install --import` on the `default` network. Find the IP with `virsh domifaddr dispatch`.

## Proxmox VE

Copy the `.qcow2` to the Proxmox host and run **as root** (`qm` requires it), then (pick an unused VMID, e.g. 9000):
```bash
./import-proxmox.sh dispatch-appliance.qcow2 9000 --storage local-lvm --bridge vmbr0 --start
```
Creates a q35/OVMF VM, imports the disk as `scsi0`, and sets it to boot.

## First boot

On first start the appliance gives itself a **unique** SQL SA password, configures and starts SQL Server Express, and starts Dispatch (the database and schema are created automatically). This takes a couple of minutes the first time.

Then browse to the dashboard:

```
https://<vm-ip>:8420
```

(The VM gets its IP via DHCP - see it in Hyper-V Manager or with `ip a` on the console.) It's a self-signed certificate, so accept the browser warning. **Set the admin password on the first login.**

Default ports: SMTP **25** and **587**, ingestion API **8025**, dashboard **8420** - change them in the dashboard. (The appliance binds the standard SMTP ports out of the box; it falls back to **2525** only if 25 is somehow already in use.)

## Logins (there are two)

| | Username | Password | Change it |
|---|---|---|---|
| **OS** (console / SSH) | `ubuntu` | `dispatch` | forced on first login; later `passwd` |
| **Dashboard** (web UI) | - (single admin) | set on first visit to `:8420` | **System → About → Change password** |

SSH is enabled with password auth (`ssh ubuntu@<vm-ip>`). The dashboard password is stored only as a bcrypt hash.

## Static IP

It boots on DHCP, but a relay your apps point at should have a fixed address - set one early. A baked-in helper, **`dispatch-set-ip`**, pins an address for you - it auto-detects the NIC, writes the netplan file (mode 600), applies it, and prints the new dashboard URL:

```bash
# interactive - just answer the prompts:
sudo dispatch-set-ip

# or in one line (DNS defaults to 1.1.1.1,8.8.8.8):
sudo dispatch-set-ip -a 192.168.1.50/24 -g 192.168.1.1 -d 1.1.1.1,8.8.8.8

# revert to automatic:
sudo dispatch-set-ip --dhcp
```

Pass `-i <nic>` to target a specific interface (see `ip a`); run `dispatch-set-ip -h` for all options.

## Maintenance (console)

- Service: `systemctl status dispatch` · logs: `journalctl -u dispatch -f` and `/var/log/dispatch/`.
- SQL: `systemctl status mssql-server`.
- Config (connection string only): `/var/lib/dispatch/appsettings.json` - everything else is in the dashboard.

## Security notes

- Every appliance generates its **own** SQL SA password, at-rest encryption key (`.dispatch-key`), dashboard TLS cert, SSH host keys, and machine-id on first boot - nothing secret is shared across downloads.
- The **OS login** (`ubuntu`/`dispatch`) is a known default but **must be changed on first login** - do so immediately, especially before exposing the VM beyond a trusted LAN.
- The dashboard admin password is set by **you** on first login and is stored only as a bcrypt hash in the database.
- The appliance bundles **SQL Server Express**, which is free; its use is governed by Microsoft's SQL Server Express license terms.

## Building it yourself

CI builds all three formats in `.github/workflows/appliance.yml`. Locally, on a Linux host with `libguestfs-tools` + `qemu-utils`:

```bash
dotnet publish src/Dispatch.Service -c Release -r linux-x64 --self-contained true -o publish/linux
sudo PREBUILT_DIR="$PWD/publish/linux" VERSION=1.2.3 LIBGUESTFS_BACKEND=direct \
  OUT="$PWD/dispatch-appliance.vhdx" \
  OVA_OUT="$PWD/dispatch-appliance.ova" \
  QCOW2_OUT="$PWD/dispatch-appliance.qcow2" \
  ./appliance/build-appliance.sh
```

It customizes the official Ubuntu cloud image **offline** with libguestfs (no Hyper-V host / nested virt needed): one qcow2 is built, then emitted as VHDX (Hyper-V), a stream-optimized-VMDK OVA (VMware), and a compressed qcow2 (KVM/Proxmox). `OVA_OUT`/`QCOW2_OUT` are optional. See `build-appliance.sh`, `provision.sh` (in-guest build steps), and `firstboot.sh` (per-VM first-boot setup). CI boots the image under QEMU/UEFI to verify it reaches `/health`; the OVA's structure (OVF XML + manifest) is validated but its VMware import is a manual check.
