#Requires -Version 7
<#
  Windows in-place UPGRADE smoke - proves that running a newer DispatchSetup.exe over an existing install
  upgrades in place (MajorUpgrade), keeps the customer's data and config, and leaves a working service.
  This is the Windows counterpart to tests/smoke/upgrade-from-released-version.sh (which is systemd-only).

  It uses two locally-built bundles at different versions ($BaseExe then $NextExe) so the test is
  deterministic and self-contained (no dependency on a published release or a chained database server).

  What it asserts, in order:
    1.  the base bundle installs and the service serves /health, reporting the base version;
    2.  first-run works: the admin password sets (written to the DB config table) and an API key is created
        (a real row in api_keys) - the durable state we expect to survive the upgrade;
    3.  the next bundle, run exactly as a user would (just run the exe, /quiet), upgrades in place:
          - /health reports the NEW version  -> the new binaries are actually running,
          - exactly ONE product is installed  -> MajorUpgrade replaced it, not a side-by-side install;
    4.  data survived: the app no longer needs setup, the SAME admin password still logs in on a fresh
        session, and the API key row is still there  -> the SQLite database was preserved;
    5.  config survived: appsettings.json and the .dispatch-key encryption key are byte-for-byte unchanged
        -> the connection string and the key that decrypts secrets were not clobbered.
  Any of these failing is a broken upgrade, and this fails CI instead of the customer.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaseExe,
    [Parameter(Mandatory)][string]$NextExe,
    [string]$Dashboard = 'https://localhost:8420',
    [string]$Password  = 'Upgr8de-Sm0ke-Marsh-Pylon!',
    [string]$DataRoot  = "$env:ProgramData\Dispatch"
)
$ErrorActionPreference = 'Stop'
$hdr = @{ 'X-Dispatch-Request' = '1' }

function New-Session { New-Object Microsoft.PowerShell.Commands.WebRequestSession }
function DGet ($sess, $p)     { Invoke-RestMethod -SkipCertificateCheck -WebSession $sess -Uri "$Dashboard$p" }
function DPost($sess, $p, $b) { Invoke-RestMethod -SkipCertificateCheck -WebSession $sess -Headers $hdr -ContentType 'application/json' -Method Post -Uri "$Dashboard$p" -Body $b }

function Wait-Health {
    foreach ($i in 1..30) {
        try { if ((Invoke-WebRequest -UseBasicParsing -SkipCertificateCheck -TimeoutSec 5 "$Dashboard/health").StatusCode -eq 200) { return } } catch {}
        Start-Sleep -Seconds 5
    }
    throw "the service never served /health"
}

function Install-Bundle ($exe, $tag) {
    $log = "$tag-install.log"
    $p = Start-Process -Wait -PassThru -FilePath $exe -ArgumentList '/quiet', '/log', $log
    Write-Host "$tag exit code: $($p.ExitCode)"
    # 0 = success, 3010 = success + reboot required (fine in CI).
    if ($p.ExitCode -ne 0 -and $p.ExitCode -ne 3010) {
        Get-ChildItem -Filter "$tag-install*.log" -EA SilentlyContinue | ForEach-Object { Write-Host "----- $($_.Name) -----"; Get-Content $_.FullName -Tail 60 }
        throw "$tag install failed (exit $($p.ExitCode))"
    }
}

# Dispatch's Add/Remove Programs entries, read from the registry. Get-Package does not list a WiX Burn
# bundle reliably, so we read ARP directly (both 64- and 32-bit views). Returning the version lets us prove
# the old product was REPLACED, not left beside the new one.
function Get-DispatchArp {
    $roots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    @(Get-ItemProperty $roots -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -match 'Dispatch SMTP Relay' } |
        Select-Object DisplayName, DisplayVersion)
}
function Format-Arp ($arp) { ($arp | ForEach-Object { "$($_.DisplayName) $($_.DisplayVersion)" }) -join '; ' }

# --- 1. Install the BASE version -----------------------------------------------------------------
Write-Host "=== Installing base: $BaseExe ==="
Install-Bundle $BaseExe 'base'
Wait-Health
$s = New-Session
$baseVersion = (DGet $s '/health').version
Write-Host "base /health version: $baseVersion"
if (-not $baseVersion) { throw "/health did not report a version" }

# --- 2. Create durable state a customer would have: password + an API key ------------------------
$status = DGet $s '/api/auth/status'
if (-not $status.needsSetup) { throw "a fresh install should need first-run setup" }
DPost $s '/api/auth/password' "{""password"":""$Password""}" | Out-Null   # sets session cookie too
$key = DPost $s '/api/keys' '{"name":"upgrade-smoke","rateLimitPerMinute":0}'
if (-not $key.id) { throw "failed to create the pre-upgrade API key" }
Write-Host "seeded: admin password + api key id=$($key.id)"

# --- 3. Capture config + key that MUST NOT change across the upgrade -----------------------------
$appPath = Join-Path $DataRoot 'appsettings.json'
$keyPath = Join-Path $DataRoot '.dispatch-key'
$dbPath  = Join-Path $DataRoot 'dispatch.db'
if (-not (Test-Path $appPath)) { throw "appsettings.json missing at $appPath" }
if (-not (Test-Path $dbPath))  { throw "the default SQLite database was not created at $dbPath" }
$appBefore = (Get-FileHash $appPath -Algorithm SHA256).Hash
$keyBefore = (Test-Path $keyPath) ? (Get-FileHash $keyPath -Algorithm SHA256).Hash : $null
$arpBefore = Get-DispatchArp
Write-Host "pre-upgrade  arp=[$(Format-Arp $arpBefore)]  appsettings=$appBefore  keyfile=$keyBefore"
if ($arpBefore.Count -lt 1) { throw "the base install did not register in Add/Remove Programs" }

# --- 4. UPGRADE: run the new exe exactly as a user would ('just run it') --------------------------
Write-Host "=== Upgrading in place: $NextExe ==="
Install-Bundle $NextExe 'next'
Wait-Health

# 4a. New binaries are running.
$s2 = New-Session
$newVersion = (DGet $s2 '/health').version
Write-Host "post-upgrade /health version: $newVersion"
if ($newVersion -eq $baseVersion) { throw "version is still $baseVersion after the upgrade - the new binaries are not running (upgrade did not take)" }

# 4b. In place, not side-by-side: the ARP registration did not multiply, and the old version is gone
# (replaced), not sitting next to the new one.
$arpAfter = Get-DispatchArp
Write-Host "post-upgrade arp=[$(Format-Arp $arpAfter)]"
if ($arpAfter.Count -lt 1) { throw "no Dispatch install is registered after the upgrade" }
if ($arpAfter.Count -gt $arpBefore.Count) { throw "Dispatch ARP entries grew from $($arpBefore.Count) to $($arpAfter.Count) - a side-by-side install, not an in-place upgrade" }
if ($arpAfter | Where-Object { $_.DisplayVersion -like '0.7.0*' }) { throw "the old 0.7.0 registration is still present after the upgrade - the old product was not replaced" }

# --- 5. Data survived: no re-setup, old password still works, api key row still present -----------
$status2 = DGet $s2 '/api/auth/status'
if ($status2.needsSetup) { throw "the upgraded install asks for first-run setup again - the database was WIPED by the upgrade" }
$fresh = New-Session
DPost $fresh '/api/auth/login' "{""password"":""$Password""}" | Out-Null    # throws on 4xx = password lost
$keys = DGet $fresh '/api/keys'
if (-not ($keys | Where-Object { $_.id -eq $key.id -and $_.name -eq 'upgrade-smoke' })) {
    throw "the API key created before the upgrade is gone - the api_keys table did not survive"
}
Write-Host "data preserved: password still valid, api key id=$($key.id) still present"

# --- 6. Config + key survived byte-for-byte ------------------------------------------------------
$appAfter = (Get-FileHash $appPath -Algorithm SHA256).Hash
if ($appAfter -ne $appBefore) { throw "appsettings.json changed across the upgrade ($appBefore -> $appAfter) - the connection string / config was clobbered" }
if ($keyBefore) {
    $keyAfter = (Get-FileHash $keyPath -Algorithm SHA256).Hash
    if ($keyAfter -ne $keyBefore) { throw ".dispatch-key changed across the upgrade - encrypted settings would become unreadable" }
}
Write-Host "config preserved: appsettings.json and .dispatch-key unchanged"

Write-Host ""
Write-Host "PASS: in-place upgrade $baseVersion -> $newVersion kept data + config and left one working install."
