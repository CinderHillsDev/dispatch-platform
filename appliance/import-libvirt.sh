#!/usr/bin/env bash
#
# Import the Dispatch SMTP Relay appliance qcow2 into KVM/libvirt as a UEFI VM (virt-install --import).
# The appliance configures PostgreSQL + Dispatch on first boot; then browse to https://<vm-ip>:8420.
#
# Usage:
#   sudo ./import-libvirt.sh dispatch-appliance.qcow2 [--name dispatch] [--memory 4096] [--vcpus 2]
#                                                     [--network default] [--start]
#
set -euo pipefail

QCOW2="${1:?usage: import-libvirt.sh <appliance.qcow2> [options]}"; shift || true
NAME="dispatch"; MEMORY=4096; VCPUS=2; NETWORK="default"; START=0
while [ $# -gt 0 ]; do
  case "$1" in
    --name) NAME="$2"; shift 2;;
    --memory) MEMORY="$2"; shift 2;;
    --vcpus) VCPUS="$2"; shift 2;;
    --network) NETWORK="$2"; shift 2;;
    --start) START=1; shift;;
    *) echo "unknown option: $1" >&2; exit 1;;
  esac
done

[ -f "$QCOW2" ] || { echo "qcow2 not found: $QCOW2" >&2; exit 1; }
# Needs root: copies into the system libvirt images pool (/var/lib/libvirt/images), chowns it, and defines a
# system VM (qemu:///system). Re-run with sudo. (libvirt-group users still can't write the system pool.)
[ "$(id -u)" = 0 ] || { echo "Run with sudo: $0 writes to /var/lib/libvirt/images and defines a system VM." >&2; exit 1; }
command -v virt-install >/dev/null || { echo "virt-install not found (install virtinst/virt-install)." >&2; exit 1; }

# Copy into the default libvirt images pool so libvirt owns/labels it (SELinux/AppArmor friendly).
POOL="/var/lib/libvirt/images"
dest="$POOL/${NAME}.qcow2"
mkdir -p "$POOL"
echo "==> Copying $QCOW2 -> $dest"
cp -f "$QCOW2" "$dest"
command -v virt-customize >/dev/null && true
chown libvirt-qemu:kvm "$dest" 2>/dev/null || chown qemu:qemu "$dest" 2>/dev/null || true

run_flag=""; [ "$START" = 1 ] || run_flag="--noreboot"
echo "==> Creating VM '$NAME' (UEFI, ${VCPUS} vCPU, ${MEMORY} MB, network '$NETWORK')"
virt-install \
  --name "$NAME" \
  --memory "$MEMORY" \
  --vcpus "$VCPUS" \
  --os-variant ubuntu24.04 \
  --boot uefi \
  --disk path="$dest",format=qcow2,bus=virtio \
  --network network="$NETWORK",model=virtio \
  --channel unix,target_type=virtio,name=org.qemu.guest_agent.0 \
  --graphics none --console pty,target_type=serial \
  --import --noautoconsole $run_flag
# --channel org.qemu.guest_agent.0 adds the guest-agent virtio channel; the appliance's baked-in
# qemu-guest-agent then reports the VM's IP to libvirt ("virsh domifaddr <vm> --source agent").

echo
echo "VM '$NAME' defined. First boot configures PostgreSQL + Dispatch (a few minutes)."
[ "$START" = 1 ] || echo "Start it with:  virsh start $NAME"
echo "Find its IP with:  virsh domifaddr $NAME"
echo "Then browse to https://<vm-ip>:8420 and set the admin password."
