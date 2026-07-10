==============================================================================
 Dispatch SMTP Relay - KVM / Proxmox appliance
==============================================================================

This zip contains:
  * dispatch-appliance.qcow2         the disk image (UEFI; Ubuntu 64-bit)
  * dispatch-appliance.qcow2.sha256  checksum
  * import-libvirt.sh                one-command import for libvirt / virsh (KVM)
  * import-proxmox.sh                one-command import for Proxmox VE
  * README.txt                       this file

------------------------------------------------------------------------------
 Import - libvirt / KVM
------------------------------------------------------------------------------
  Run on the hypervisor host:
    sudo ./import-libvirt.sh          # creates + starts a UEFI VM from the qcow2

------------------------------------------------------------------------------
 Import - Proxmox VE
------------------------------------------------------------------------------
  Run on the Proxmox host:
    sudo ./import-proxmox.sh          # creates a VM and imports the qcow2 disk

  Open either script to see options (VM name, memory, network bridge, etc.).

------------------------------------------------------------------------------
 After import
------------------------------------------------------------------------------
  * Start the VM. First boot configures PostgreSQL + Dispatch (allow a
    few minutes). The VM uses DHCP.
  * Browse to https://<vm-ip>:8420 (self-signed cert - accept the warning) and
    SET THE ADMIN PASSWORD on the first login.
  * Full docs: https://chrismuench.github.io/Dispatch-SMTP-Relay/
==============================================================================
