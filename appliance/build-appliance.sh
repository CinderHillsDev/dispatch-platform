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
  --run "$REPO/appliance/provision.sh" \
  || { echo "=== provision FAILED — trace tail ==="; virt-cat -a "$WORK/disk.qcow2" /var/log/dispatch-provision.log 2>/dev/null | tail -80; exit 1; }
echo "--- provision trace tail ---"; virt-cat -a "$WORK/disk.qcow2" /var/log/dispatch-provision.log 2>/dev/null | tail -50

echo "==> Verifying the image (bootloader + key files present)"
echo "ESP /EFI/BOOT:"; virt-ls -a "$WORK/disk.qcow2" /boot/efi/EFI/BOOT 2>/dev/null || echo "  (none!)"
echo "ESP /EFI/ubuntu:"; virt-ls -a "$WORK/disk.qcow2" /boot/efi/EFI/ubuntu 2>/dev/null || echo "  (none!)"
virt-ls -a "$WORK/disk.qcow2" /boot/efi/EFI/BOOT 2>/dev/null | grep -qi '^BOOTX64.EFI$' \
  || { echo "ERROR: UEFI fallback bootloader \EFI\BOOT\BOOTX64.EFI missing — image would not boot on empty-NVRAM firmware" >&2; exit 1; }

echo "--- /opt ---"; virt-ls -a "$WORK/disk.qcow2" /opt 2>/dev/null || echo "(none)"
echo "--- /opt/dispatch (install dir) ---"; virt-ls -a "$WORK/disk.qcow2" /opt/dispatch 2>/dev/null | head -5 || echo "(none)"
echo "--- install.sh captured output (/tmp/install.log) ---"; virt-cat -a "$WORK/disk.qcow2" /tmp/install.log 2>/dev/null | tail -40 || echo "(no install.log)"
echo "--- /etc/systemd/system (dispatch unit files) ---"; virt-ls -a "$WORK/disk.qcow2" /etc/systemd/system | grep -i dispatch || echo "(no dispatch unit files!)"
echo "--- /etc/systemd/system/multi-user.target.wants ---"; virt-ls -a "$WORK/disk.qcow2" /etc/systemd/system/multi-user.target.wants || echo "(listing failed)"
for unit in dispatch.service dispatch-firstboot.service; do
  virt-ls -a "$WORK/disk.qcow2" /etc/systemd/system/multi-user.target.wants | grep -qx "$unit" \
    || { echo "ERROR: $unit is not enabled (no WantedBy symlink) — it would not start at boot" >&2; exit 1; }
done

echo "==> Converting to a Gen2/UEFI dynamic VHDX"
qemu-img convert -p -f qcow2 -O vhdx -o subformat=dynamic "$WORK/disk.qcow2" "$OUT"

echo "==> Built $OUT"
ls -lh "$OUT"
