#requires -RunAsAdministrator
<#
.SYNOPSIS
  Import the Dispatch SMTP Relay appliance VHDX as a ready-to-run Hyper-V VM.

.DESCRIPTION
  Creates a Generation 2 VM, copies the appliance VHDX into the VM's storage, sets the Secure Boot
  template to the Microsoft UEFI Certificate Authority (required for Linux), connects a network switch,
  and (optionally) starts it. The appliance configures SQL Server + Dispatch on first boot; browse to
  https://<vm-ip>:8420 and set the admin password.

.EXAMPLE
  .\Import-DispatchAppliance.ps1 -VhdxPath .\dispatch-appliance.vhdx

.EXAMPLE
  .\Import-DispatchAppliance.ps1 -VhdxPath .\dispatch-appliance.vhdx -Name "Dispatch" -MemoryGB 6 -SwitchName "External" -Start
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)] [string] $VhdxPath,
  [string] $Name = "Dispatch SMTP Relay",
  [int]    $MemoryGB = 4,
  [int]    $CpuCount = 2,
  [string] $SwitchName,
  [string] $VmPath,
  [switch] $Start
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $VhdxPath)) { throw "VHDX not found: $VhdxPath" }
$VhdxPath = (Resolve-Path $VhdxPath).Path
if (Get-VM -Name $Name -ErrorAction SilentlyContinue) { throw "A VM named '$Name' already exists." }

# Pick a switch if not given: prefer the well-known 'Default Switch' (NAT+DHCP), else the first external one.
if (-not $SwitchName) {
  $sw = Get-VMSwitch -ErrorAction SilentlyContinue |
        Sort-Object @{ Expression = { $_.Name -eq 'Default Switch' } } -Descending |
        Select-Object -First 1
  if (-not $sw) { throw "No Hyper-V switch found. Create one (or pass -SwitchName)." }
  $SwitchName = $sw.Name
}
Write-Host "Using network switch: $SwitchName"

# Place the VM's disk under the host's VM storage (or -VmPath), copying the appliance VHDX so the original
# stays pristine and re-importable.
if (-not $VmPath) { $VmPath = (Get-VMHost).VirtualMachinePath }
$destDir  = Join-Path $VmPath $Name
New-Item -ItemType Directory -Force -Path $destDir | Out-Null
$destVhdx = Join-Path $destDir ([IO.Path]::GetFileName($VhdxPath))
Write-Host "Copying VHDX -> $destVhdx"
Copy-Item -Path $VhdxPath -Destination $destVhdx -Force

Write-Host "Creating Generation 2 VM '$Name'"
$vm = New-VM -Name $Name -Generation 2 -MemoryStartupBytes ($MemoryGB * 1GB) `
             -VHDPath $destVhdx -SwitchName $SwitchName -Path $VmPath
Set-VMProcessor -VM $vm -Count $CpuCount

# Linux on Gen2 needs the Microsoft UEFI CA Secure Boot template (not the default Windows one).
Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate "MicrosoftUEFICertificateAuthority"

# SQL Server needs a stable working set; disable Dynamic Memory.
Set-VMMemory -VM $vm -DynamicMemoryEnabled $false

Write-Host ""
Write-Host "Created '$Name' (Gen2, $CpuCount vCPU, $MemoryGB GB, switch '$SwitchName')."
if ($Start) {
  Start-VM -VM $vm
  Write-Host "Started. First boot configures SQL + Dispatch (a few minutes)."
} else {
  Write-Host "Start it with:  Start-VM -Name '$Name'"
}
Write-Host "Then browse to https://<vm-ip>:8420 and set the admin password."
