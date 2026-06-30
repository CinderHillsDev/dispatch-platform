<#
.SYNOPSIS
  Import the Dispatch SMTP Relay appliance VHDX as a ready-to-run Hyper-V VM.

.DESCRIPTION
  Creates a Generation 2 VM, copies the appliance VHDX into the chosen storage location, sets the Secure
  Boot template to the Microsoft UEFI Certificate Authority (required for Linux), connects a virtual
  switch (optionally on a specific VLAN), and (optionally) starts it. The appliance configures SQL Server
  + Dispatch on first boot; browse to https://<vm-ip>:8420 and set the admin password.

  Run it with no networking/storage flags for a guided menu: it lists the host's virtual switches and
  storage volumes and prompts for the VLAN, memory, and CPU. Pass -SwitchName for fully unattended use.

.EXAMPLE
  # Guided menu (pick switch + storage, optional VLAN):
  .\Import-DispatchAppliance.ps1 -VhdxPath .\dispatch-appliance.vhdx

.EXAMPLE
  # Unattended:
  .\Import-DispatchAppliance.ps1 -VhdxPath .\dispatch-appliance.vhdx -Name "Dispatch" -MemoryGB 6 `
      -SwitchName "External" -VlanId 20 -VmPath "D:\Hyper-V" -Start
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)] [string] $VhdxPath,
  [string] $Name = "Dispatch SMTP Relay",
  [int]    $MemoryGB = 4,
  [int]    $CpuCount = 2,
  [string] $SwitchName,
  [ValidateRange(0, 4094)] [int] $VlanId = 0,   # 0 = untagged / no VLAN
  [string] $VmPath,
  [switch] $Interactive,
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
  Write-Host "  (The appliance disk is ~6-10 GB thin-provisioned; SQL + logs grow it over time.)"
  return (Read-WithDefault "VM storage folder" $Default)
}

# --- preflight ----------------------------------------------------------------------------------
# Managing Hyper-V needs either an elevated Administrator session or membership in the local "Hyper-V
# Administrators" group (well-known SID S-1-5-32-578) — the latter can run the cmdlets WITHOUT elevation.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin     = $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
$isHyperVAdm = $principal.IsInRole([Security.Principal.SecurityIdentifier]::new("S-1-5-32-578"))
if (-not ($isAdmin -or $isHyperVAdm)) {
  throw "This needs an elevated Administrator session or membership in the 'Hyper-V Administrators' group. Re-run as administrator, or have an admin add you to that group."
}

if (-not (Test-Path $VhdxPath)) { throw "VHDX not found: $VhdxPath" }
$VhdxPath = (Resolve-Path $VhdxPath).Path

# Guided menu when no switch was specified (or -Interactive): collect name, switch, VLAN, storage, sizing.
$useMenu = $Interactive -or (-not $SwitchName)
if ($useMenu) {
  Write-Host "=== Dispatch SMTP Relay — Hyper-V import ==="
  $Name       = Read-WithDefault "VM name" $Name
  $SwitchName = Select-VmSwitch
  $VlanId     = Read-VlanId
  $VmPath     = Select-Storage ($(if ($VmPath) { $VmPath } else { (Get-VMHost).VirtualMachinePath }))
  $MemoryGB   = Read-IntWithDefault "Memory (GB)" $MemoryGB
  $CpuCount   = Read-IntWithDefault "vCPU count" $CpuCount
  if (-not $Start) { $Start = (Read-WithDefault "Start the VM after import? (y/N)" "N") -match '^(y|yes)$' }
}
else {
  Write-Host "Using network switch: $SwitchName"
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
if ($useMenu) {
  if ((Read-WithDefault "Proceed? (Y/n)" "Y") -notmatch '^(y|yes)$') { Write-Host "Cancelled."; return }
}

# --- create -------------------------------------------------------------------------------------
# Everything for this VM lives under one folder named after it: <chosen storage>\<VM name>\ — holding both
# the VM config (New-VM -Path) and the copied VHDX. This keeps each VM self-contained (matching the Hyper-V
# wizard's "store the VM in a different location") and the original appliance VHDX stays pristine.
$destDir  = Join-Path $VmPath $Name
New-Item -ItemType Directory -Force -Path $destDir | Out-Null
$destVhdx = Join-Path $destDir ([IO.Path]::GetFileName($VhdxPath))
Write-Host "Copying VHDX -> $destVhdx"
Copy-Item -Path $VhdxPath -Destination $destVhdx -Force

Write-Host "Creating Generation 2 VM '$Name'"
$vm = New-VM -Name $Name -Generation 2 -MemoryStartupBytes ($MemoryGB * 1GB) `
             -VHDPath $destVhdx -SwitchName $SwitchName -Path $destDir
Set-VMProcessor -VM $vm -Count $CpuCount

# Linux on Gen2 needs the Microsoft UEFI CA Secure Boot template (not the default Windows one).
Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate "MicrosoftUEFICertificateAuthority"

# SQL Server needs a stable working set; disable Dynamic Memory.
Set-VMMemory -VM $vm -DynamicMemoryEnabled $false

# Apply a VLAN tag to the adapter if requested (access mode = single tagged VLAN).
if ($VlanId -gt 0) {
  Set-VMNetworkAdapterVlan -VMName $Name -Access -VlanId $VlanId
  Write-Host "Network adapter set to VLAN access mode, VLAN ID $VlanId."
}

Write-Host ""
Write-Host ("Created '{0}' (Gen2, {1} vCPU, {2} GB, switch '{3}'{4})." -f `
  $Name, $CpuCount, $MemoryGB, $SwitchName, $(if ($VlanId -gt 0) { ", VLAN $VlanId" } else { "" }))
if ($Start) {
  Start-VM -VM $vm
  Write-Host "Started. First boot configures SQL + Dispatch (a few minutes)."
} else {
  Write-Host "Start it with:  Start-VM -Name '$Name'"
}
Write-Host "Then browse to https://<vm-ip>:8420 and set the admin password."
