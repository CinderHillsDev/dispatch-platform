#!/bin/sh
#
# Dispatch appliance — in-guest provisioning, run by virt-customize during the image build (NOT at runtime).
# virt-customize runs --run scripts with /bin/sh, so this is POSIX sh (no bashisms). Runs with NO network
# (libguestfs's passt networking is unreliable on CI runners): the SQL Server Express + tools packages are
# pre-downloaded on the host into /opt/stage/debs (see build-appliance.sh) and installed offline here. SQL is
# only unpacked — it's configured per-VM on first boot (appliance/firstboot.sh).
#
set -eu
export DEBIAN_FRONTEND=noninteractive
# Capture stderr + an execution trace to a file so the host build can virt-cat it (virt-customize suppresses
# a --run script's output on success, which has been hiding where this stops).
exec 2>/var/log/dispatch-provision.log
set -x
STAGE="/opt/stage"

echo "==> Accept Microsoft EULAs (debconf) for SQL Server tools"
echo "msodbcsql18 msodbcsql/ACCEPT_EULA boolean true" | debconf-set-selections
echo "mssql-tools18 mssql-tools/accept_eula boolean true" | debconf-set-selections

echo "==> Install SQL Server Express + tools (offline from pre-downloaded .debs)"
dpkg -i "$STAGE"/debs/*.deb || true   # may report ordering warnings; configure resolves them
dpkg --configure -a
test -x /opt/mssql/bin/mssql-conf || { echo "ERROR: mssql-server did not install" >&2; exit 1; }
test -x /opt/mssql-tools18/bin/sqlcmd || { echo "ERROR: mssql-tools18 did not install" >&2; exit 1; }

echo "==> Stage Dispatch (enabled, not started; SA password finalized on first boot)"
bash "$STAGE/install.sh" --prebuilt "$STAGE/bin" --no-start \
  --sql-connection "Server=localhost;Database=DispatchLog;User Id=sa;Password=__SA_PASSWORD__;TrustServerCertificate=True;Encrypt=True"

echo "==> First-boot unit + start ordering"
install -m 755 -D "$STAGE/firstboot.sh" /opt/dispatch-appliance/firstboot.sh
install -m 644 "$STAGE/dispatch-firstboot.service" /etc/systemd/system/dispatch-firstboot.service
mkdir -p /etc/systemd/system/dispatch.service.d
# Dispatch must start only after first-boot configures SQL.
printf '[Unit]\nAfter=dispatch-firstboot.service\nRequires=dispatch-firstboot.service\n' \
  > /etc/systemd/system/dispatch.service.d/10-appliance.conf
# Enable both units offline. `systemctl --root=/` creates the WantedBy symlinks without a running systemd
# (a plain `systemctl enable` is a silent no-op in the build appliance), and the explicit ln is a fallback.
# Without this neither first-boot nor Dispatch starts at boot (VM comes up to a bare login — the boot smoke
# caught exactly that).
systemctl --root=/ enable dispatch-firstboot.service dispatch.service 2>&1 || echo "WARN: systemctl --root enable returned nonzero"
mkdir -p /etc/systemd/system/multi-user.target.wants
ln -sf ../dispatch-firstboot.service /etc/systemd/system/multi-user.target.wants/dispatch-firstboot.service
ln -sf ../dispatch.service /etc/systemd/system/multi-user.target.wants/dispatch.service
echo "PROVISION wants dir:"; ls -la /etc/systemd/system/multi-user.target.wants/ | grep -i dispatch || echo "  (no dispatch symlinks!)"

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
  echo "   WARN: $ESP/ubuntu not found (ESP not mounted?) — skipping fallback bootloader"
fi

echo "==> cloud-init: no cloud metadata service on Hyper-V — avoid boot-time probing delays"
mkdir -p /etc/cloud/cloud.cfg.d
printf 'datasource_list: [ NoCloud, None ]\n' > /etc/cloud/cloud.cfg.d/99-dispatch-datasource.cfg

echo "==> Cleanup"
rm -rf "$STAGE"
# Empty machine-id so each VM generates a unique one on first boot.
: > /etc/machine-id
echo "==> provisioning complete"
