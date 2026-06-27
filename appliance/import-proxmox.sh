#!/usr/bin/env bash
#
# Import the Dispatch SMTP Relay appliance qcow2 into Proxmox VE as a UEFI (OVMF) VM. Run on the Proxmox
# host. The appliance configures SQL Server + Dispatch on first boot; then browse to https://<vm-ip>:8420.
#
# Usage:
#   ./import-proxmox.sh dispatch-appliance.qcow2 <vmid> [--storage local-lvm] [--bridge vmbr0]
#                                                       [--name dispatch] [--memory 4096] [--cores 2] [--start]
#
set -euo pipefail

QCOW2="${1:?usage: import-proxmox.sh <appliance.qcow2> <vmid> [options]}"
VMID="${2:?usage: import-proxmox.sh <appliance.qcow2> <vmid> [options]}"
shift 2 || true
STORAGE="local-lvm"; BRIDGE="vmbr0"; NAME="dispatch"; MEMORY=4096; CORES=2; START=0
while [ $# -gt 0 ]; do
  case "$1" in
    --storage) STORAGE="$2"; shift 2;;
    --bridge) BRIDGE="$2"; shift 2;;
    --name) NAME="$2"; shift 2;;
    --memory) MEMORY="$2"; shift 2;;
    --cores) CORES="$2"; shift 2;;
    --start) START=1; shift;;
    *) echo "unknown option: $1" >&2; exit 1;;
  esac
done

[ -f "$QCOW2" ] || { echo "qcow2 not found: $QCOW2" >&2; exit 1; }
[ "$(id -u)" = 0 ] || { echo "Run this on the Proxmox VE host as root — qm requires it. Try: sudo $0 ..." >&2; exit 1; }
command -v qm >/dev/null || { echo "qm not found — run this on a Proxmox VE host." >&2; exit 1; }
qm status "$VMID" >/dev/null 2>&1 && { echo "VMID $VMID already exists." >&2; exit 1; }

echo "==> Creating VM $VMID ($NAME): q35 + OVMF/UEFI, ${CORES} cores, ${MEMORY} MB, bridge $BRIDGE"
qm create "$VMID" --name "$NAME" --memory "$MEMORY" --cores "$CORES" \
  --machine q35 --bios ovmf --scsihw virtio-scsi-pci \
  --net0 "virtio,bridge=$BRIDGE" \
  --efidisk0 "$STORAGE:0,efitype=4m,pre-enrolled-keys=0"

echo "==> Importing the appliance disk into $STORAGE"
qm importdisk "$VMID" "$QCOW2" "$STORAGE"

# importdisk attaches it as an unused disk; wire it up as scsi0 and boot from it.
disk="$(qm config "$VMID" | sed -n 's/^unused0: *//p')"
[ -n "$disk" ] || { echo "ERROR: could not find the imported disk (unused0) on VM $VMID" >&2; exit 1; }
echo "==> Attaching $disk as scsi0 and setting boot order"
qm set "$VMID" --scsi0 "$disk"
qm set "$VMID" --boot order=scsi0

echo
echo "VM $VMID ($NAME) created. First boot configures SQL + Dispatch (a few minutes)."
if [ "$START" = 1 ]; then qm start "$VMID"; echo "Started."; else echo "Start it with:  qm start $VMID"; fi
echo "Then browse to https://<vm-ip>:8420 and set the admin password."
