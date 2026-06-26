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
if ! bash "$STAGE/install.sh" --prebuilt "$STAGE/bin" --no-start \
      --sql-connection "Server=localhost;Database=DispatchLog;User Id=sa;Password=__SA_PASSWORD__;TrustServerCertificate=True;Encrypt=True" \
      >/tmp/install.log 2>&1; then
  rc=$?
  echo "install.sh FAILED (rc=$rc) — output follows:"
  cat /tmp/install.log
  exit "$rc"
fi
cat /tmp/install.log

echo "==> First-boot unit + start ordering"
install -m 755 -D "$STAGE/firstboot.sh" /opt/dispatch-appliance/firstboot.sh
install -m 644 "$STAGE/dispatch-firstboot.service" /etc/systemd/system/dispatch-firstboot.service
mkdir -p /etc/systemd/system/dispatch.service.d
# Dispatch must start only after first-boot configures SQL.
printf '[Unit]\nAfter=dispatch-firstboot.service\nRequires=dispatch-firstboot.service\n' \
  > /etc/systemd/system/dispatch.service.d/10-appliance.conf
systemctl enable dispatch-firstboot.service

echo "==> cloud-init: no cloud metadata service on Hyper-V — avoid boot-time probing delays"
mkdir -p /etc/cloud/cloud.cfg.d
printf 'datasource_list: [ NoCloud, None ]\n' > /etc/cloud/cloud.cfg.d/99-dispatch-datasource.cfg

echo "==> Cleanup"
rm -rf "$STAGE"
# Empty machine-id so each VM generates a unique one on first boot.
: > /etc/machine-id
echo "==> provisioning complete"
