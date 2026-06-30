==============================================================================
 Dispatch SMTP Relay - Hyper-V appliance
==============================================================================

This zip contains:
  * dispatch-appliance.vhdx          the ready-to-run virtual disk (Linux, Gen2/UEFI)
  * Import-DispatchAppliance.ps1     a one-step import helper for Hyper-V
  * README.txt                       this file

------------------------------------------------------------------------------
 Requirements
------------------------------------------------------------------------------
  * Windows with the Hyper-V role enabled.
  * Run PowerShell either as an elevated Administrator, OR as a member of the
    local "Hyper-V Administrators" group (which can manage Hyper-V without
    elevation). The script checks for one of these.

------------------------------------------------------------------------------
 Quick start (guided menu)
------------------------------------------------------------------------------
  1. Unzip this file to a folder.
  2. Open PowerShell in that folder and run:

       .\Import-DispatchAppliance.ps1 -VhdxPath .\dispatch-appliance.vhdx

     With no networking flags it walks you through it:
       - pick one of the host's virtual switches,
       - choose the storage volume/folder for the VM (shows free space),
       - optionally set a VLAN ID,
       - set memory and vCPU,
     then it confirms and creates the VM.

  3. If you didn't choose to start it during the import:
       Start-VM -Name "Dispatch SMTP Relay"

  4. Find the VM's IP (Hyper-V Manager, or `ip a` on the VM console - the VM
     uses DHCP), then browse to:
       https://<vm-ip>:8420
     It uses a self-signed certificate, so accept the browser warning, and
     SET THE ADMIN PASSWORD on the first login.

------------------------------------------------------------------------------
 Unattended import
------------------------------------------------------------------------------
  Pass -SwitchName to skip the menu; add any of -VlanId / -VmPath / -MemoryGB /
  -CpuCount / -Start:

    .\Import-DispatchAppliance.ps1 -VhdxPath .\dispatch-appliance.vhdx `
        -SwitchName "External" -VlanId 20 -VmPath "D:\Hyper-V" -MemoryGB 6 -Start

------------------------------------------------------------------------------
 Manual import (no script)
------------------------------------------------------------------------------
  Hyper-V Manager -> New -> Virtual Machine:
    * Generation 2
    * Memory: 4096 MB (disable Dynamic Memory)
    * Connect a virtual switch
    * "Use an existing virtual hard disk" -> dispatch-appliance.vhdx
  Then VM Settings -> Security -> Secure Boot -> Template:
    "Microsoft UEFI Certificate Authority"   (required for Linux)
  (To tag a VLAN: VM Settings -> Network Adapter -> VLAN ID.)

------------------------------------------------------------------------------
 Notes
------------------------------------------------------------------------------
  * First boot configures SQL Server Express + Dispatch; allow a few minutes.
  * The appliance is self-contained - no separate .NET runtime needed.
  * Full docs: https://chrismuench.github.io/Dispatch-SMTP-Relay/
==============================================================================
