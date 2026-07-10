#!/usr/bin/env bash
#
# Dispatch SMTP Relay - Hyper-V appliance builder (Ubuntu 24.04 LTS).
#
# Customizes the official Ubuntu cloud image offline with libguestfs (no Hyper-V host, no nested virt) and
# converts it to a Gen2/UEFI dynamic VHDX. PostgreSQL's binaries are baked in; each VM configures the
# database with a unique password and starts Dispatch on first boot (see appliance/firstboot.sh).
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

echo "==> Verifying the cloud image against Ubuntu's GPG-signed SHA256SUMS"
# The base image is the entire OS of every appliance, so verify it fail-closed: check the checksum file's
# detached signature against the PINNED Ubuntu Cloud Image signing key, then check the image hash against it.
img_base="$(dirname "$UBUNTU_IMG_URL")"
img_name="$(basename "$UBUNTU_IMG_URL")"
curl -fSL "$img_base/SHA256SUMS"     -o "$WORK/SHA256SUMS"
curl -fSL "$img_base/SHA256SUMS.gpg" -o "$WORK/SHA256SUMS.gpg"
UBUNTU_CLOUD_FPR="D2EB44626FDDC30B513D5BB71A5D6C4C7DB87C81"   # Ubuntu Cloud Image Builder signing key
export GNUPGHOME="$WORK/gnupg"; mkdir -p "$GNUPGHOME"; chmod 700 "$GNUPGHOME"
gpg --batch --keyserver hkps://keyserver.ubuntu.com --recv-keys "$UBUNTU_CLOUD_FPR" 2>/dev/null \
  || gpg --batch --keyserver hkps://keys.openpgp.org --recv-keys "$UBUNTU_CLOUD_FPR" \
  || { echo "ERROR: could not fetch the Ubuntu cloud-image signing key $UBUNTU_CLOUD_FPR" >&2; exit 1; }
gpg --batch --status-fd 1 --verify "$WORK/SHA256SUMS.gpg" "$WORK/SHA256SUMS" 2>/dev/null \
  | grep -q "VALIDSIG .*$UBUNTU_CLOUD_FPR" \
  || { echo "ERROR: SHA256SUMS signature did not verify against the pinned Ubuntu key" >&2; exit 1; }
want="$(grep " [*]\?${img_name}\$" "$WORK/SHA256SUMS" | awk '{print $1}')"
got="$(sha256sum "$WORK/base.img" | awk '{print $1}')"
[ -n "$want" ] && [ "$want" = "$got" ] \
  || { echo "ERROR: cloud image checksum mismatch (want='$want' got='$got')" >&2; exit 1; }
echo "cloud image verified: $img_name ($got)"

echo "==> Assembling the staging payload"
STAGE="$WORK/stage"
mkdir -p "$STAGE/bin"
cp -a "$PREBUILT_DIR/." "$STAGE/bin/"
cp "$REPO/installer/linux/install.sh"            "$STAGE/install.sh"
cp "$REPO/installer/linux/dispatch.service"      "$STAGE/dispatch.service"
# Web-UI updater files (so the appliance can apply uploaded upgrade packages); install.sh installs them.
cp "$REPO/installer/linux/dispatch-updater.service" "$STAGE/dispatch-updater.service"
cp "$REPO/installer/linux/dispatch-update.path"     "$STAGE/dispatch-update.path"
cp "$REPO/installer/linux/dispatch-update.sh"       "$STAGE/dispatch-update.sh"
cp "$REPO/src/Dispatch.Core/Updates/dispatch-update-public.pem" "$STAGE/dispatch-update-public.pem"
cp "$REPO/appliance/firstboot.sh"                "$STAGE/firstboot.sh"
cp "$REPO/appliance/dispatch-firstboot.service"  "$STAGE/dispatch-firstboot.service"
cp "$REPO/appliance/dispatch-set-ip"             "$STAGE/dispatch-set-ip"
chmod +x "$STAGE/install.sh" "$STAGE/dispatch-update.sh" "$STAGE/firstboot.sh" "$STAGE/dispatch-set-ip" "$STAGE/bin/Dispatch.Service"

echo "==> Detecting the guest kernel version (for Hyper-V integration tools, which are kernel-tied)"
# The Hyper-V KVP/VSS/fcopy daemons ship in a per-kernel package, so we must fetch the set matching THIS
# image's kernel (not the download container's). open-vm-tools / qemu-guest-agent are kernel-independent.
KVER="$(virt-ls -a "$WORK/base.img" /lib/modules 2>/dev/null | grep -E '^[0-9]' | sort -V | tail -1 || true)"
echo "guest kernel: ${KVER:-<unknown - Hyper-V IP reporting may be skipped>}"

echo "==> Pre-downloading PostgreSQL (.debs) for an offline in-guest install"
# Done on the host in a clean Ubuntu 24.04 container so the dependency closure matches the cloud image and
# the image build itself needs no in-guest network (libguestfs passt networking is unreliable on CI).
mkdir -p "$STAGE/debs"
docker run --rm -e KVER="$KVER" -v "$STAGE/debs:/debs" ubuntu:24.04 bash -ec '
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq curl ca-certificates >/dev/null
  # PostgreSQL 16 ships in the default Ubuntu 24.04 repos, so no extra apt source or signing key is needed.
  cd /debs
  # Full recursive runtime dependency closure of the target packages (skip virtual/undownloadable entries).
  deps=$(apt-cache depends --recurse --no-recommends --no-suggests --no-conflicts --no-breaks --no-replaces --no-enhances \
           postgresql postgresql-contrib | grep "^\w" | sort -u)
  for d in $deps; do apt-get download "$d" 2>/dev/null || true; done
  echo "downloaded $(ls -1 /debs/*.deb | wc -l) PostgreSQL packages"
  ls /debs/postgresql*_*.deb >/dev/null   # fail the build if the core package is missing

  # Hypervisor guest agents so each hypervisor manager can display the VM IP (installed offline in the guest;
  # NO first-boot network, so this keeps the appliance no-call-home): open-vm-tools = VMware, qemu-guest-agent
  # = KVM/libvirt/Proxmox (both kernel-independent), and the Hyper-V KVP/VSS/fcopy daemons via the per-kernel
  # linux-cloud-tools set matching THIS image (linux-cloud-tools-common holds the systemd units). Best-effort:
  # a missing Hyper-V set only drops Hyper-V IP reporting, it does not fail the build.
  agents="open-vm-tools qemu-guest-agent linux-cloud-tools-common"
  [ -n "$KVER" ] && agents="$agents linux-cloud-tools-$KVER"
  gdeps=$(apt-cache depends --recurse --no-recommends --no-suggests --no-conflicts --no-breaks --no-replaces --no-enhances \
            $agents 2>/dev/null | grep "^\w" | sort -u || true)
  for d in $gdeps; do apt-get download "$d" 2>/dev/null || true; done
  ls /debs/open-vm-tools_*.deb    >/dev/null 2>&1 || echo "WARN: open-vm-tools not downloaded (VMware IP display may be unavailable)"
  ls /debs/qemu-guest-agent_*.deb >/dev/null 2>&1 || echo "WARN: qemu-guest-agent not downloaded (KVM/Proxmox IP display may be unavailable)"
  ls /debs/linux-cloud-tools-*-generic_*.deb >/dev/null 2>&1 || echo "WARN: Hyper-V cloud-tools for kernel '"'"'$KVER'"'"' not downloaded (Hyper-V IP display may be unavailable)"
  echo "total .debs staged: $(ls -1 /debs/*.deb | wc -l)"
'

echo "==> Expanding the root partition into a ${DISK_SIZE} working image"
qemu-img create -f qcow2 "$WORK/disk.qcow2" "$DISK_SIZE"
virt-resize --expand /dev/sda1 "$WORK/base.img" "$WORK/disk.qcow2"

echo "==> Customizing the image (PostgreSQL + Dispatch + first-boot)"
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
  || { echo "ERROR: UEFI fallback bootloader \EFI\BOOT\BOOTX64.EFI missing - image would not boot on empty-NVRAM firmware" >&2; exit 1; }

for unit in dispatch.service dispatch-firstboot.service; do
  virt-ls -a "$WORK/disk.qcow2" /etc/systemd/system/multi-user.target.wants 2>/dev/null | grep -qx "$unit" \
    || { echo "ERROR: $unit is not enabled (no WantedBy symlink) - it would not start at boot" >&2; exit 1; }
done
echo "verified: bootloader fallback + dispatch/firstboot units enabled"

# Informational: which hypervisor guest agents ended up enabled (best-effort - never fails the build). At
# least open-vm-tools + qemu-guest-agent should be present; the Hyper-V KVP daemon requires the kernel-tied
# cloud-tools set to have been available for this image's kernel.
enabled_wants="$(virt-ls -a "$WORK/disk.qcow2" /etc/systemd/system/multi-user.target.wants 2>/dev/null || true)"
for unit in hv-kvp-daemon.service open-vm-tools.service qemu-guest-agent.service; do
  echo "$enabled_wants" | grep -qx "$unit" && echo "guest agent enabled: $unit" || echo "guest agent NOT enabled: $unit"
done

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

  # OVA = tar of ovf, then mf, then disk - in that order (required by the OVF spec / ovftool).
  ( cd "$ova_dir" && tar -cf "$OVA_OUT" "$ovf" "$mf" "$vmdk" )
  echo "==> Built $OVA_OUT"; ls -lh "$OVA_OUT"
fi
