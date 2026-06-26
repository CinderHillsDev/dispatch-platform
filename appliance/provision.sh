#!/usr/bin/env bash
#
# Dispatch appliance — in-guest provisioning, run by virt-customize during the image build (NOT at runtime).
# It installs SQL Server Express's package (binaries only; configured per-VM on first boot), stages Dispatch
# via install.sh --no-start, and installs the first-boot unit. The staging dir (/opt/stage) is copied in by
# build-appliance.sh and removed at the end.
#
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
STAGE="/opt/stage"
UBUNTU_REL="24.04"

echo "==> base tooling"
apt-get update
apt-get install -y curl gnupg apt-transport-https ca-certificates

echo "==> Microsoft package repositories (SQL Server 2022 + tools)"
curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg
curl -fsSL "https://packages.microsoft.com/config/ubuntu/${UBUNTU_REL}/mssql-server-2022.list" \
  | sed 's|deb |deb [signed-by=/usr/share/keyrings/microsoft-prod.gpg] |' > /etc/apt/sources.list.d/mssql-server-2022.list
curl -fsSL "https://packages.microsoft.com/config/ubuntu/${UBUNTU_REL}/prod.list" \
  | sed 's|deb |deb [signed-by=/usr/share/keyrings/microsoft-prod.gpg] |' > /etc/apt/sources.list.d/microsoft-prod.list
apt-get update

echo "==> SQL Server Express (package only — configured on first boot) + sqlcmd"
# The mssql-server postinst installs binaries but does not run setup; mssql-conf setup runs on first boot
# (appliance/firstboot.sh) so each VM gets a unique SA password.
ACCEPT_EULA=Y apt-get install -y mssql-server
ACCEPT_EULA=Y apt-get install -y mssql-tools18 unixodbc-dev

echo "==> Hyper-V guest integration daemons (best-effort)"
apt-get install -y linux-cloud-tools-virtual hyperv-daemons 2>/dev/null \
  || apt-get install -y linux-cloud-tools-virtual 2>/dev/null \
  || echo "   (Hyper-V daemons unavailable; the in-kernel hv_* drivers still provide net/disk/console)"

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
systemctl enable dispatch-firstboot.service

echo "==> cloud-init: no cloud metadata service on Hyper-V — avoid boot-time probing delays"
mkdir -p /etc/cloud/cloud.cfg.d
printf 'datasource_list: [ NoCloud, None ]\n' > /etc/cloud/cloud.cfg.d/99-dispatch-datasource.cfg

echo "==> Cleanup"
rm -rf "$STAGE"
apt-get clean
rm -rf /var/lib/apt/lists/*
# Empty machine-id so each VM generates a unique one on first boot.
: > /etc/machine-id
echo "==> provisioning complete"
