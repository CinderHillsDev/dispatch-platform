#!/usr/bin/env bash
#
# Dispatch SMTP Relay — Hyper-V appliance builder (Ubuntu 24.04 LTS).
#
# Customizes the official Ubuntu cloud image offline with libguestfs (no Hyper-V host, no nested virt) and
# converts it to a Gen2/UEFI dynamic VHDX. SQL Server Express's binaries are baked in; each VM configures
# SQL with a unique SA password and starts Dispatch on first boot (see appliance/firstboot.sh).
#
# Requires (host): libguestfs-tools, qemu-utils, curl.
# Usage:
#   PREBUILT_DIR=publish/linux VERSION=1.2.3 ./appliance/build-appliance.sh
#
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"

PREBUILT_DIR="${PREBUILT_DIR:?set PREBUILT_DIR to the self-contained linux-x64 publish dir (contains Dispatch.Service)}"
VERSION="${VERSION:-0.0.0-dev}"
DISK_SIZE="${DISK_SIZE:-20G}"
OUT="${OUT:-dispatch-appliance-${VERSION}-x64.vhdx}"
UBUNTU_IMG_URL="${UBUNTU_IMG_URL:-https://cloud-images.ubuntu.com/releases/24.04/release/ubuntu-24.04-server-cloudimg-amd64.img}"

[ -f "$PREBUILT_DIR/Dispatch.Service" ] || { echo "PREBUILT_DIR has no Dispatch.Service: $PREBUILT_DIR" >&2; exit 1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
export LIBGUESTFS_BACKEND=direct

echo "==> Downloading Ubuntu cloud image"
curl -fSL "$UBUNTU_IMG_URL" -o "$WORK/base.img"

echo "==> Assembling the staging payload"
STAGE="$WORK/stage"
mkdir -p "$STAGE/bin"
cp -a "$PREBUILT_DIR/." "$STAGE/bin/"
cp "$REPO/installer/linux/install.sh"            "$STAGE/install.sh"
cp "$REPO/installer/linux/dispatch.service"      "$STAGE/dispatch.service"
cp "$REPO/appliance/firstboot.sh"                "$STAGE/firstboot.sh"
cp "$REPO/appliance/dispatch-firstboot.service"  "$STAGE/dispatch-firstboot.service"
chmod +x "$STAGE/install.sh" "$STAGE/firstboot.sh" "$STAGE/bin/Dispatch.Service"

echo "==> Expanding the root partition into a ${DISK_SIZE} working image"
qemu-img create -f qcow2 "$WORK/disk.qcow2" "$DISK_SIZE"
virt-resize --expand /dev/sda1 "$WORK/base.img" "$WORK/disk.qcow2"

echo "==> Customizing the image (SQL Server Express + Dispatch + first-boot)"
virt-customize -a "$WORK/disk.qcow2" \
  --hostname dispatch \
  --copy-in "$STAGE:/opt" \
  --run "$REPO/appliance/provision.sh"

echo "==> Converting to a Gen2/UEFI dynamic VHDX"
qemu-img convert -p -f qcow2 -O vhdx -o subformat=dynamic "$WORK/disk.qcow2" "$OUT"

echo "==> Built $OUT"
ls -lh "$OUT"
