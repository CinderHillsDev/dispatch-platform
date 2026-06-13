<#
.SYNOPSIS
  Dispatch SMTP Relay — Windows installer (scripted).

.DESCRIPTION
  Publishes the service, writes config to %ProgramData%\Dispatch, registers a Windows Service, and
  opens the firewall. The SQL connection string and the dashboard admin password are supplied at
  install time (the admin password is required — you'll be prompted if it isn't passed).

  Run from an elevated PowerShell:
    .\install.ps1 -SqlConnection "Server=...;Database=DispatchLog;User Id=sa;Password=...;TrustServerCertificate=True;Encrypt=True"

  NOTE: This script has not been executed on the build machine (macOS). It is the Windows install path
  and should be validated on Windows. A WiX MSI (Dispatch.wxs) is also provided for packaged installs.
#>
param(
  [Parameter(Mandatory = $true)][string]$SqlConnection,
  [string]$AdminPassword,
  [int]$HttpPort = 8420,
  [int]$ApiPort = 8421,
  [string]$SmtpPorts = "25,587",
  [string]$Source
)

$ErrorActionPreference = "Stop"
$InstallDir = "$Env:ProgramFiles\Dispatch"
$DataDir = "$Env:ProgramData\Dispatch"

if (-not $AdminPassword) {
  $secure = Read-Host -AsSecureString "Set the dashboard admin password"
  $AdminPassword = [System.Net.NetworkCredential]::new("", $secure).Password
  if (-not $AdminPassword) { throw "Admin password is required." }
}

if (-not $Source) { $Source = (Resolve-Path "$PSScriptRoot\..\..").Path }

Write-Host "==> Building the web UI"
Push-Location "$Source\src\Dispatch.UI"; npm ci; npm run build; Pop-Location
Remove-Item -Recurse -Force "$Source\src\Dispatch.Web\wwwroot" -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$Source\src\Dispatch.Web\wwwroot" | Out-Null
Copy-Item -Recurse -Force "$Source\src\Dispatch.UI\dist\*" "$Source\src\Dispatch.Web\wwwroot\"

Write-Host "==> Publishing the service to $InstallDir"
dotnet publish "$Source\src\Dispatch.Service" -c Release -o $InstallDir

Write-Host "==> Writing config to $DataDir"
New-Item -ItemType Directory -Force "$DataDir\spool", "$DataDir\logs" | Out-Null
$smtpJson = ($SmtpPorts -split ',' | ForEach-Object { $_.Trim() }) -join ', '
$config = @{
  ConnectionStrings = @{ DispatchLog = $SqlConnection }
  AdminPassword     = $AdminPassword
  Spool             = @{ Directory = "$DataDir\spool"; WorkerCount = 4 }
  Listener          = @{ Ports = @($SmtpPorts -split ',' | ForEach-Object { [int]$_.Trim() }); AllowedCidrs = @("127.0.0.1/32", "::1/128") }
  Api               = @{ Port = $ApiPort }
  WebUi             = @{ Port = $HttpPort }
}
$config | ConvertTo-Json -Depth 6 | Set-Content "$DataDir\appsettings.json" -Encoding UTF8

Write-Host "==> Registering the Windows service"
$bin = "`"$InstallDir\Dispatch.Service.exe`" --contentRoot `"$DataDir`""
sc.exe stop Dispatch 2>$null | Out-Null
sc.exe delete Dispatch 2>$null | Out-Null
New-Service -Name Dispatch -BinaryPathName $bin -DisplayName "Dispatch SMTP Relay" -StartupType Automatic | Out-Null
# Point the log directory at ProgramData (the service's CWD is system32).
$envBlock = [string[]]@("DISPATCH_LOG_DIR=$DataDir\logs", "DOTNET_ENVIRONMENT=Production")
New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\Dispatch" -Name Environment -PropertyType MultiString -Value $envBlock -Force | Out-Null

Write-Host "==> Opening firewall ports"
foreach ($p in @($HttpPort, $ApiPort) + ($SmtpPorts -split ',' | ForEach-Object { [int]$_.Trim() })) {
  New-NetFirewallRule -DisplayName "Dispatch $p" -Direction Inbound -Action Allow -Protocol TCP -LocalPort $p -ErrorAction SilentlyContinue | Out-Null
}

Start-Service Dispatch
Write-Host "`nDispatch is installed and running. Dashboard: http://localhost:$HttpPort"
