#Requires -Version 5
# Dispatch SMTP Relay - Windows update applier. Written to the data dir and launched (decoupled, as SYSTEM,
# via Task Scheduler) by the service after it verifies + stages an uploaded upgrade package. Swaps the
# installed binaries, restarts the service, and rolls back if the new version fails to start. Runs under
# Windows PowerShell 5.1 (no openssl / -SkipCertificateCheck): the package signature was already verified
# by the service; here we recheck the payload's SHA-256 against the signed manifest and use the service's
# SCM Running state (UseWindowsService) as the health gate.
$ErrorActionPreference = 'Stop'
$updatesDir = $PSScriptRoot
$req = Join-Path $updatesDir 'apply.request'
$script:ver = ''
$installDir = $null

function Set-Status([string]$state, [string]$msg) {
  $o = [ordered]@{ state = $state; version = $script:ver; message = $msg; updatedAtUtc = (Get-Date).ToUniversalTime().ToString('o') }
  Set-Content -LiteralPath (Join-Path $updatesDir 'status.json') -Value ($o | ConvertTo-Json -Compress) -Encoding UTF8
}

if (-not (Test-Path -LiteralPath $req)) { exit 0 }

try {
  $r = Get-Content -LiteralPath $req -Raw | ConvertFrom-Json
  $script:ver = $r.version
  $staged = $r.stagedDir

  # Install dir from the registered service's image path; fall back to Program Files\Dispatch.
  $img = (Get-CimInstance Win32_Service -Filter "Name='Dispatch'" -ErrorAction SilentlyContinue).PathName
  if ($img -and ($img -match '"?(.+\\)Dispatch\.Service\.exe')) { $installDir = $Matches[1].TrimEnd('\') }
  else { $installDir = Join-Path $env:ProgramFiles 'Dispatch' }

  # Recheck the payload hash against the (already signature-verified) manifest.
  $man = Get-Content -LiteralPath (Join-Path $staged 'manifest.json') -Raw | ConvertFrom-Json
  $want = ($man.artifacts.'win-x64'.sha256).ToLower()
  $got = (Get-FileHash -LiteralPath (Join-Path $staged 'payload') -Algorithm SHA256).Hash.ToLower()
  if ($want -ne $got) { Set-Status 'Failed' 'payload checksum mismatch'; Remove-Item -LiteralPath $req -Force; exit 1 }

  Set-Status 'Applying' "applying $script:ver"
  Stop-Service Dispatch -Force
  for ($i = 0; $i -lt 30 -and (Get-Service Dispatch).Status -ne 'Stopped'; $i++) { Start-Sleep -Seconds 1 }

  # Back up current binaries so we can roll back, then extract the new payload over the install dir.
  $backup = Join-Path $installDir '.backup'
  if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $backup | Out-Null
  Get-ChildItem -LiteralPath $installDir -Force | Where-Object { $_.Name -ne '.backup' } |
    Copy-Item -Destination $backup -Recurse -Force
  Expand-Archive -LiteralPath (Join-Path $staged 'payload') -DestinationPath $installDir -Force

  # UseWindowsService(): Start-Service returns once the new version signals Running to the SCM (after DB
  # migrations + listeners) - that is the health gate.
  Set-Status 'Restarting' "restarting service on $script:ver (the dashboard will briefly disconnect)"
  Start-Service Dispatch
  Start-Sleep -Seconds 3
  if ((Get-Service Dispatch).Status -ne 'Running') { throw 'service is not Running after start' }

  Set-Status 'Succeeded' "updated to $script:ver"
  Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $req -Force
}
catch {
  $err = $_.Exception.Message
  try {
    $backup = if ($installDir) { Join-Path $installDir '.backup' } else { $null }
    if ($backup -and (Test-Path -LiteralPath $backup)) {
      Stop-Service Dispatch -Force -ErrorAction SilentlyContinue
      Copy-Item -Path (Join-Path $backup '*') -Destination $installDir -Recurse -Force
      Start-Service Dispatch -ErrorAction SilentlyContinue
      Set-Status 'RolledBack' "update to $script:ver failed: $err; rolled back to the previous version"
    } else {
      Set-Status 'Failed' "update to $script:ver failed: $err"
    }
  } catch { Set-Status 'Failed' "update + rollback failed: $($_.Exception.Message)" }
  Remove-Item -LiteralPath $req -Force -ErrorAction SilentlyContinue
  exit 1
}
finally {
  schtasks /Delete /TN DispatchUpdate /F 2>$null | Out-Null
}
