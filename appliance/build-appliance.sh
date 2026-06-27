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
cp "$REPO/appliance/dispatch-set-ip"             "$STAGE/dispatch-set-ip"
chmod +x "$STAGE/install.sh" "$STAGE/firstboot.sh" "$STAGE/dispatch-set-ip" "$STAGE/bin/Dispatch.Service"

echo "==> Pre-downloading SQL Server Express + tools (.debs) for an offline in-guest install"
# Done on the host in a clean Ubuntu 24.04 container so the dependency closure matches the cloud image and
# the image build itself needs no in-guest network (libguestfs passt networking is unreliable on CI).
mkdir -p "$STAGE/debs"
docker run --rm -v "$STAGE/debs:/debs" ubuntu:24.04 bash -ec '
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq curl ca-certificates >/dev/null
  # packages-microsoft-prod.deb sets up the Microsoft "prod" repo + signing key (msodbcsql18, mssql-tools18).
  curl -fsSL https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -o /tmp/pmc.deb
  dpkg -i /tmp/pmc.deb >/dev/null
  # The SQL Server engine has its own per-version feed; Ubuntu 24.04 ships SQL Server 2025 (2022 is 22.04).
  curl -fsSL https://packages.microsoft.com/config/ubuntu/24.04/mssql-server-2025.list \
    -o /etc/apt/sources.list.d/mssql-server.list
  apt-get update -qq
  # Full recursive runtime dependency closure of the target packages (skip virtual/undownloadable entries).
  deps=$(apt-cache depends --recurse --no-recommends --no-suggests --no-conflicts --no-breaks --no-replaces --no-enhances \
           mssql-server mssql-tools18 unixodbc-dev | grep "^\w" | sort -u)
  cd /debs
  for d in $deps; do apt-get download "$d" 2>/dev/null || true; done
  echo "downloaded $(ls -1 /debs/*.deb | wc -l) packages"
  ls /debs/mssql-server_*.deb >/dev/null   # fail the build if the core package is missing
'

echo "==> Expanding the root partition into a ${DISK_SIZE} working image"
qemu-img create -f qcow2 "$WORK/disk.qcow2" "$DISK_SIZE"
virt-resize --expand /dev/sda1 "$WORK/base.img" "$WORK/disk.qcow2"

echo "==> Customizing the image (SQL Server Express + Dispatch + first-boot)"
# --no-network: provisioning installs pre-downloaded .debs offline, so the appliance needs no in-guest
# network (and we avoid libguestfs's passt networking, which fails on CI runners).
virt-customize -a "$WORK/disk.qcow2" \
  --no-network \
  --hostname dispatch \
  --copy-in "$STAGE:/opt" \
  --run "$REPO/appliance/provision.sh"

echo "==> Verifying the image (bootloader + key files present)"
echo "ESP /EFI/BOOT:"; virt-ls -a "$WORK/disk.qcow2" /boot/efi/EFI/BOOT 2>/dev/null || echo "  (none!)"
echo "ESP /EFI/ubuntu:"; virt-ls -a "$WORK/disk.qcow2" /boot/efi/EFI/ubuntu 2>/dev/null || echo "  (none!)"
virt-ls -a "$WORK/disk.qcow2" /boot/efi/EFI/BOOT 2>/dev/null | grep -qi '^BOOTX64.EFI$' \
  || { echo "ERROR: UEFI fallback bootloader \EFI\BOOT\BOOTX64.EFI missing — image would not boot on empty-NVRAM firmware" >&2; exit 1; }

for unit in dispatch.service dispatch-firstboot.service; do
  virt-ls -a "$WORK/disk.qcow2" /etc/systemd/system/multi-user.target.wants 2>/dev/null | grep -qx "$unit" \
    || { echo "ERROR: $unit is not enabled (no WantedBy symlink) — it would not start at boot" >&2; exit 1; }
done
echo "verified: bootloader fallback + dispatch/firstboot units enabled"

echo "==> Converting to a Gen2/UEFI dynamic VHDX"
qemu-img convert -p -f qcow2 -O vhdx -o subformat=dynamic "$WORK/disk.qcow2" "$OUT"
echo "==> Built $OUT"; ls -lh "$OUT"

# Optional: a compressed qcow2 for KVM/libvirt/Proxmox (the native format). Set QCOW2_OUT to enable.
if [ -n "${QCOW2_OUT:-}" ]; then
  echo "==> Building the qcow2 (KVM/Proxmox): $QCOW2_OUT"
  qemu-img convert -p -f qcow2 -O qcow2 -c "$WORK/disk.qcow2" "$QCOW2_OUT"
  echo "==> Built $QCOW2_OUT"; ls -lh "$QCOW2_OUT"
fi

# Optional: a VMware (vSphere/ESXi) OVA from the same image. Set OVA_OUT to enable.
if [ -n "${OVA_OUT:-}" ]; then
  echo "==> Building the VMware OVA: $OVA_OUT"
  ova_dir="$WORK/ova"; mkdir -p "$ova_dir"
  vmdk="dispatch-appliance-disk1.vmdk"; ovf="dispatch-appliance.ovf"; mf="dispatch-appliance.mf"

  # Stream-optimized VMDK (the only VMDK subformat valid inside an OVA).
  qemu-img convert -p -f qcow2 -O vmdk -o subformat=streamOptimized "$WORK/disk.qcow2" "$ova_dir/$vmdk"

  capacity="$(qemu-img info --output=json "$WORK/disk.qcow2" | sed -n 's/.*"virtual-size": *\([0-9]*\).*/\1/p' | head -1)"
  vmdk_size="$(stat -c%s "$ova_dir/$vmdk")"
  sed -e "s|@@VMDK_FILE@@|$vmdk|" -e "s|@@VMDK_FILE_SIZE@@|$vmdk_size|" \
      -e "s|@@DISK_CAPACITY@@|$capacity|" -e "s|@@VERSION@@|$VERSION|" \
      "$REPO/appliance/dispatch.ovf.template" > "$ova_dir/$ovf"

  # Manifest: SHA256 of the ovf then the vmdk (OVF spec format).
  ( cd "$ova_dir" && printf 'SHA256(%s)= %s\n' "$ovf" "$(sha256sum "$ovf" | cut -d' ' -f1)" >  "$mf"
                     printf 'SHA256(%s)= %s\n' "$vmdk" "$(sha256sum "$vmdk" | cut -d' ' -f1)" >> "$mf" )

  # OVA = tar of ovf, then mf, then disk — in that order (required by the OVF spec / ovftool).
  ( cd "$ova_dir" && tar -cf "$OVA_OUT" "$ovf" "$mf" "$vmdk" )
  echo "==> Built $OVA_OUT"; ls -lh "$OVA_OUT"
fi
