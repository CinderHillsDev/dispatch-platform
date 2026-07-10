<#
.SYNOPSIS
  Import the Dispatch SMTP Relay appliance VHDX as a ready-to-run Hyper-V VM.

.DESCRIPTION
  Creates a Generation 2 VM, copies the appliance VHDX into the chosen storage location, sets the Secure
  Boot template to the Microsoft UEFI Certificate Authority (required for Linux), connects a virtual
  switch (optionally on a specific VLAN), and (optionally) starts it. The appliance configures PostgreSQL
  + Dispatch on first boot; browse to https://<vm-ip>:8420 and set the admin password.

  Run it with no networking/storage flags for a guided menu: it lists the host's virtual switches and
  storage volumes and prompts for the VLAN, memory, and CPU. Pass -SwitchName for fully unattended use.

  On a failover-cluster host it detects the cluster and offers to make the VM highly available (default
  yes); unattended it adds the VM to the cluster automatically unless -NoCluster is passed. HA requires
  the VM's storage to be on cluster shared storage (CSV).

.EXAMPLE
  # Guided menu - finds the .vhdx next to this script automatically:
  .\Import-DispatchAppliance.ps1

.EXAMPLE
  # Unattended (pass -SwitchName to skip the menu; -VhdxPath only if it's not next to the script):
  .\Import-DispatchAppliance.ps1 -Name "Dispatch" -MemoryGB 6 -SwitchName "External" -VlanId 20 -VmPath "D:\Hyper-V" -Start
#>
[CmdletBinding()]
param(
  # Optional: defaults to the .vhdx sitting next to this script (the zip ships them together).
  [string] $VhdxPath,
  [string] $Name = "Dispatch SMTP Relay",
  [int]    $MemoryGB = 4,
  [int]    $CpuCount = 2,
  [string] $SwitchName,
  [ValidateRange(0, 4094)] [int] $VlanId = 0,   # 0 = untagged / no VLAN
  [string] $VmPath,
  [switch] $Interactive,
  [switch] $NoCluster,    # skip making the VM highly available even on a failover-cluster host
  [switch] $Start
)

$ErrorActionPreference = "Stop"

# --- helpers ------------------------------------------------------------------------------------
function Read-WithDefault([string]$Prompt, [string]$Default) {
  $v = Read-Host "$Prompt [$Default]"
  if ([string]::IsNullOrWhiteSpace($v)) { return $Default } else { return $v }
}

function Read-IntWithDefault([string]$Prompt, [int]$Default) {
  while ($true) {
    $v = Read-Host "$Prompt [$Default]"
    if ([string]::IsNullOrWhiteSpace($v)) { return $Default }
    if ($v -match '^\d+$' -and [int]$v -gt 0) { return [int]$v }
    Write-Host "  Enter a positive whole number."
  }
}

function Select-VmSwitch {
  $switches = @(Get-VMSwitch -ErrorAction SilentlyContinue | Sort-Object Name)
  if (-not $switches) { throw "No Hyper-V virtual switches found. Create one in Hyper-V Manager, or pass -SwitchName." }
  Write-Host ""
  Write-Host "Available virtual switches:"
  for ($i = 0; $i -lt $switches.Count; $i++) {
    $s = $switches[$i]
    $extra = if ($s.SwitchType -eq 'External' -and $s.NetAdapterInterfaceDescription) { " - $($s.NetAdapterInterfaceDescription)" } else { "" }
    Write-Host ("  [{0}] {1}  ({2}){3}" -f ($i + 1), $s.Name, $s.SwitchType, $extra)
  }
  while ($true) {
    $sel = Read-Host "Select a switch by number"
    if ($sel -match '^\d+$' -and [int]$sel -ge 1 -and [int]$sel -le $switches.Count) { return $switches[[int]$sel - 1].Name }
    Write-Host "  Enter a number between 1 and $($switches.Count)."
  }
}

function Read-VlanId {
  while ($true) {
    $v = Read-Host "VLAN ID (1-4094, blank = none/untagged)"
    if ([string]::IsNullOrWhiteSpace($v)) { return 0 }
    if ($v -match '^\d+$' -and [int]$v -ge 1 -and [int]$v -le 4094) { return [int]$v }
    Write-Host "  Enter a VLAN ID between 1 and 4094, or leave blank for none."
  }
}

function Select-Storage([string]$Default) {
  Write-Host ""
  Write-Host "Storage volumes:"
  Get-Volume -ErrorAction SilentlyContinue |
    Where-Object { $_.DriveLetter -and $_.Size -gt 0 } | Sort-Object DriveLetter | ForEach-Object {
      Write-Host ("  {0}:  {1:N0} GB free of {2:N0} GB  {3}" -f $_.DriveLetter, ($_.SizeRemaining / 1GB), ($_.Size / 1GB), $_.FileSystemLabel)
    }
  Write-Host "  (The appliance disk is ~6-10 GB thin-provisioned; PostgreSQL + logs grow it over time.)"
  return (Read-WithDefault "VM storage folder" $Default)
}

# --- preflight ----------------------------------------------------------------------------------
# Managing Hyper-V needs either an elevated Administrator session or membership in the local "Hyper-V
# Administrators" group (well-known SID S-1-5-32-578) - the latter can run the cmdlets WITHOUT elevation.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin     = $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
$isHyperVAdm = $principal.IsInRole([Security.Principal.SecurityIdentifier]::new("S-1-5-32-578"))
if (-not ($isAdmin -or $isHyperVAdm)) {
  throw "This needs an elevated Administrator session or membership in the 'Hyper-V Administrators' group. Re-run as administrator, or have an admin add you to that group."
}

# Default to the .vhdx shipped alongside this script (the appliance zip contains both). Only one is expected.
if (-not $VhdxPath) {
  $found = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter *.vhdx -File -ErrorAction SilentlyContinue)
  if ($found.Count -eq 1) { $VhdxPath = $found[0].FullName; Write-Host "Using VHDX: $VhdxPath" }
  elseif ($found.Count -eq 0) { throw "No .vhdx found next to this script. Unzip dispatch-appliance.vhdx.zip here first, or pass -VhdxPath." }
  else { throw "Multiple .vhdx files found next to this script; pass -VhdxPath to pick one." }
}
if (-not (Test-Path -LiteralPath $VhdxPath -PathType Leaf)) { throw "VHDX file not found: $VhdxPath" }
$VhdxPath = (Resolve-Path -LiteralPath $VhdxPath).Path
if ([System.IO.Path]::GetExtension($VhdxPath) -notin @('.vhdx', '.vhd')) {
  throw "-VhdxPath must point at the appliance .vhdx file, but got '$VhdxPath'. Unzip dispatch-appliance.vhdx.zip and pass the dispatch-appliance.vhdx inside it."
}

# Detect a failover cluster on this host (Get-Cluster ships in the FailoverClusters module, present only on
# cluster nodes). When clustered, offer to make the VM highly available - defaulting to yes.
$inCluster = $false
if (Get-Command Get-Cluster -ErrorAction SilentlyContinue) {
  try { $inCluster = [bool](Get-Cluster -ErrorAction Stop) } catch { $inCluster = $false }
}
$AddToCluster = $false

# Guided menu when no switch was specified (or -Interactive): collect name, switch, VLAN, storage, sizing.
$useMenu = $Interactive -or (-not $SwitchName)
if ($useMenu) {
  Write-Host "=== Dispatch SMTP Relay - Hyper-V import ==="
  $Name       = Read-WithDefault "VM name" $Name
  $SwitchName = Select-VmSwitch
  $VlanId     = Read-VlanId
  $VmPath     = Select-Storage ($(if ($VmPath) { $VmPath } else { (Get-VMHost).VirtualMachinePath }))
  $MemoryGB   = Read-IntWithDefault "Memory (GB)" $MemoryGB
  $CpuCount   = Read-IntWithDefault "vCPU count" $CpuCount
  if (-not $Start) { $Start = (Read-WithDefault "Start the VM after import? (y/N)" "N") -match '^(y|yes)$' }
  if ($inCluster -and -not $NoCluster) {
    $AddToCluster = (Read-WithDefault "This host is in a failover cluster - make the VM highly available? (Y/n)" "Y") -match '^(y|yes)$'
  }
}
else {
  Write-Host "Using network switch: $SwitchName"
  $AddToCluster = $inCluster -and (-not $NoCluster)   # unattended: HA by default on a cluster (use -NoCluster to skip)
}

if (Get-VM -Name $Name -ErrorAction SilentlyContinue) { throw "A VM named '$Name' already exists." }
if (-not (Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue)) { throw "Virtual switch '$SwitchName' not found." }
if (-not $VmPath) { $VmPath = (Get-VMHost).VirtualMachinePath }

# Confirm the plan.
Write-Host ""
Write-Host "About to create:"
Write-Host ("  Name:    {0}" -f $Name)
Write-Host ("  Sizing:  {0} vCPU, {1} GB RAM (Gen2, Dynamic Memory off)" -f $CpuCount, $MemoryGB)
Write-Host ("  Switch:  {0}{1}" -f $SwitchName, $(if ($VlanId -gt 0) { " (VLAN $VlanId)" } else { " (untagged)" }))
Write-Host ("  Storage: {0}" -f (Join-Path $VmPath $Name))
Write-Host ("  HA:      {0}" -f $(if ($AddToCluster) { "add to the failover cluster" } else { "standalone" }))
if ($useMenu) {
  if ((Read-WithDefault "Proceed? (Y/n)" "Y") -notmatch '^(y|yes)$') { Write-Host "Cancelled."; return }
}

# --- create -------------------------------------------------------------------------------------
# Everything for this VM lives under one folder named after it: <chosen storage>\<VM name>\ - holding both
# the VM config (New-VM -Path) and the copied VHDX. This keeps each VM self-contained (matching the Hyper-V
# wizard's "store the VM in a different location") and the original appliance VHDX stays pristine.
$destDir  = Join-Path $VmPath $Name
New-Item -ItemType Directory -Force -Path $destDir | Out-Null
$destVhdx = Join-Path $destDir ([IO.Path]::GetFileName($VhdxPath))
Write-Host "Copying VHDX -> $destVhdx"
Copy-Item -LiteralPath $VhdxPath -Destination $destVhdx -Force
if (-not (Test-Path -LiteralPath $destVhdx -PathType Leaf)) { throw "VHDX copy failed (destination is not a file): $destVhdx" }

Write-Host "Creating Generation 2 VM '$Name'"
try {
  $vm = New-VM -Name $Name -Generation 2 -MemoryStartupBytes ($MemoryGB * 1GB) `
               -VHDPath $destVhdx -SwitchName $SwitchName -Path $destDir
  Set-VMProcessor -VM $vm -Count $CpuCount
  # Linux on Gen2 needs the Microsoft UEFI CA Secure Boot template (not the default Windows one).
  Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate "MicrosoftUEFICertificateAuthority"
  # PostgreSQL needs a stable working set; disable Dynamic Memory.
  Set-VMMemory -VM $vm -DynamicMemoryEnabled $false
  # Apply a VLAN tag to the adapter if requested (access mode = single tagged VLAN).
  if ($VlanId -gt 0) {
    Set-VMNetworkAdapterVlan -VMName $Name -Access -VlanId $VlanId
    Write-Host "Network adapter set to VLAN access mode, VLAN ID $VlanId."
  }
}
catch {
  # Roll back a half-created VM + the copied files so the import can be retried cleanly.
  Write-Warning "Import failed; cleaning up the partial VM and copied files."
  Get-VM -Name $Name -ErrorAction SilentlyContinue | Remove-VM -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $destDir -Recurse -Force -ErrorAction SilentlyContinue
  throw
}

# Make it highly available if requested. Best-effort: a failure here (e.g. storage isn't on a CSV) leaves a
# working standalone VM rather than rolling it back. (Requires the VM's storage to be on cluster shared storage.)
if ($AddToCluster) {
  Write-Host "Adding '$Name' to the failover cluster (highly available)..."
  try {
    Add-ClusterVirtualMachineRole -VirtualMachine $Name -ErrorAction Stop | Out-Null
    Write-Host "Added to the failover cluster."
  } catch {
    Write-Warning "Could not add the VM to the cluster: $($_.Exception.Message)"
    Write-Warning "The VM is created and usable. Add it manually once its storage is on a CSV: Add-ClusterVirtualMachineRole -VirtualMachine '$Name'"
  }
}

Write-Host ""
Write-Host ("Created '{0}' (Gen2, {1} vCPU, {2} GB, switch '{3}'{4}{5})." -f `
  $Name, $CpuCount, $MemoryGB, $SwitchName, $(if ($VlanId -gt 0) { ", VLAN $VlanId" } else { "" }), $(if ($AddToCluster) { ", clustered" } else { "" }))
if ($Start) {
  Start-VM -VM $vm
  Write-Host "Started. First boot configures PostgreSQL + Dispatch (a few minutes)."
} else {
  Write-Host "Start it with:  Start-VM -Name '$Name'"
}
Write-Host "Then browse to https://<vm-ip>:8420 and set the admin password."
