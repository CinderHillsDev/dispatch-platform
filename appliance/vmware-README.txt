==============================================================================
 Dispatch SMTP Relay - VMware appliance
==============================================================================

This zip contains:
  * dispatch-appliance.ova           the importable appliance (EFI, Ubuntu 64-bit)
  * dispatch-appliance.ova.sha256    checksum
  * README.txt                       this file

------------------------------------------------------------------------------
 Import
------------------------------------------------------------------------------
  vSphere / ESXi:
    vSphere Client -> Deploy OVF Template -> select dispatch-appliance.ova ->
    accept the defaults (the descriptor already sets EFI firmware + Ubuntu 64-bit).

  Workstation / Fusion:
    File -> Open -> dispatch-appliance.ova

  CLI (ovftool):
    ovftool dispatch-appliance.ova vi://user@vcenter/Datacenter/host/esxi

------------------------------------------------------------------------------
 After import
------------------------------------------------------------------------------
  * Power on the VM. First boot configures SQL Server Express + Dispatch (allow a
    few minutes). The VM uses DHCP.
  * Browse to https://<vm-ip>:8420 (self-signed cert - accept the warning) and
    SET THE ADMIN PASSWORD on the first login.
  * Full docs: https://chrismuench.github.io/Dispatch-SMTP-Relay/
==============================================================================
