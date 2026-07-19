#!/bin/sh
#
# Dispatch appliance - in-guest provisioning, run by virt-customize during the image build (NOT at runtime).
# virt-customize runs --run scripts with /bin/sh, so this is POSIX sh (no bashisms). Runs with NO network
# (libguestfs's passt networking is unreliable on CI runners): the guest-agent packages are pre-downloaded
# on the host into /opt/stage/debs (see build-appliance.sh) and installed offline here. There is no database
# server to install - Dispatch uses the bundled SQLite file it creates under /var/lib/dispatch.
#
set -eu
export DEBIAN_FRONTEND=noninteractive
STAGE="/opt/stage"

echo "==> Install staged guest-agent packages (offline from pre-downloaded .debs)"
dpkg -i "$STAGE"/debs/*.deb || true   # may report ordering warnings; configure resolves them
# Tolerant: a guest-agent postinst can be noisy in an offline chroot (no running systemd).
dpkg --configure -a || true

echo "==> Enable hypervisor guest agents (best-effort; each idles harmlessly on a non-matching host)"
# So Hyper-V Manager / vCenter / Proxmox can show the VM's IP. The .debs are installed above; enable only the
# units that are present (systemctl --root creates the WantedBy symlinks without a running systemd). Not all
# agents install on every build (the Hyper-V set is kernel-tied and best-effort), so a missing unit is fine.
for unit in hv-kvp-daemon.service hv-vss-daemon.service hv-fcopy-daemon.service open-vm-tools.service qemu-guest-agent.service; do
  if systemctl --root=/ enable "$unit" >/dev/null 2>&1; then echo "  enabled $unit"; else echo "  (skip $unit - not installed)"; fi
done

echo "==> Stage Dispatch (enabled, not started)"
# No --sql-connection: the appliance uses the bundled SQLite database, which the service creates under
# /var/lib/dispatch on first start. That removes the whole first-boot database bootstrap the appliance used
# to need - no server to install offline, no per-VM password to generate, and a much faster first boot.
bash "$STAGE/install.sh" --prebuilt "$STAGE/bin" --no-start

install -m 755 "$STAGE/dispatch-set-ip" /usr/local/sbin/dispatch-set-ip

echo "==> Enable Dispatch at boot"
# `systemctl --root=/` creates the WantedBy symlinks without a running systemd (a plain `systemctl enable`
# is a silent no-op in the build appliance), and the explicit ln is a fallback. Without this Dispatch does
# not start at boot and the VM comes up to a bare login - the boot smoke caught exactly that.
systemctl --root=/ enable dispatch.service 2>&1 || echo "WARN: systemctl --root enable returned nonzero"
mkdir -p /etc/systemd/system/multi-user.target.wants
ln -sf ../dispatch.service /etc/systemd/system/multi-user.target.wants/dispatch.service

echo "==> Ensure the UEFI removable/fallback bootloader path exists"
# A freshly-created VM (QEMU/OVMF, or a Hyper-V Gen2 VM importing this disk) starts with empty firmware
# NVRAM, so it boots via the removable-media path \EFI\BOOT\BOOTX64.EFI. Ubuntu only installs \EFI\ubuntu\,
# so copy shim (+ grub) into the fallback path. shim keeps Secure Boot working (MS UEFI CA template).
ESP=/boot/efi/EFI
if [ -d "$ESP/ubuntu" ]; then
  mkdir -p "$ESP/BOOT"
  [ -f "$ESP/ubuntu/shimx64.efi" ] && cp -f "$ESP/ubuntu/shimx64.efi" "$ESP/BOOT/BOOTX64.EFI"
  [ -f "$ESP/ubuntu/grubx64.efi" ] && cp -f "$ESP/ubuntu/grubx64.efi" "$ESP/BOOT/grubx64.efi"
  echo "   fallback bootloader: $(ls "$ESP/BOOT" 2>/dev/null | tr '\n' ' ')"
else
  echo "   WARN: $ESP/ubuntu not found (ESP not mounted?) - skipping fallback bootloader"
fi

echo "==> cloud-init: no cloud metadata service on Hyper-V - avoid boot-time probing delays"
mkdir -p /etc/cloud/cloud.cfg.d
printf 'datasource_list: [ NoCloud, None ]\n' > /etc/cloud/cloud.cfg.d/99-dispatch-datasource.cfg

echo "==> Appliance console/SSH login: ubuntu / dispatch (must be changed on first login)"
# The cloud image's default user is locked (SSH-key only) and we inject no key, so without this nobody could
# log in to the console to set networking. cloud-init creates 'ubuntu' on first boot; give it a documented
# default password, force a change at first login, and enable SSH password auth (it's an on-LAN appliance).
# cloud-init creates the 'ubuntu' user (with sudo) on first boot; chpasswd then sets+unlocks its password
# and expires it (forced change at first login). ssh_pwauth enables SSH password auth.
cat > /etc/cloud/cloud.cfg.d/99-dispatch-login.cfg <<'CICFG'
ssh_pwauth: true
chpasswd:
  expire: true
  users:
    - name: ubuntu
      password: dispatch
      type: text
CICFG

echo "==> Cleanup"
rm -rf "$STAGE"
# Empty machine-id so each VM generates a unique one on first boot.
: > /etc/machine-id
echo "==> provisioning complete"
