#Requires -Version 7
<#
  Functional QC smoke for a real Dispatch install — the pre-release gate that proves core functionality
  actually works end to end (not just that the service starts):

    1. API delivery     — POST /api/v1/messages (HTTP, port 8025) -> Delivered via the Local relay
    2. SMTP delivery     — a real SMTP session upgraded with STARTTLS -> Delivered
    3. API key revocation — a revoked key is refused (401)
    4. SMTP AUTH         — over STARTTLS: valid creds accepted (235), bad creds rejected (535)

  Run after the service is up (serving /health). Uses the Local provider so nothing leaves the box and
  delivery is deterministic. Verifies delivery via the /stats delivered counter (the log stores spool ids
  in a different format than the API returns, so a counter delta is the robust signal).
#>
[CmdletBinding()]
param(
    [string]$Dashboard = 'https://localhost:8420',
    [string]$Api       = 'http://localhost:8025',
    [string]$Password  = 'Zq7-Marsh-Pylon-Vex!'
)
$ErrorActionPreference = 'Stop'
$hdr  = @{ 'X-Dispatch-Request' = '1' }
$sess = New-Object Microsoft.PowerShell.Commands.WebRequestSession

function DGet  ($path)       { Invoke-RestMethod -SkipCertificateCheck -WebSession $sess -Uri "$Dashboard$path" }
function DPost ($path,$body) { Invoke-RestMethod -SkipCertificateCheck -WebSession $sess -Headers $hdr -ContentType 'application/json' -Method Post -Uri "$Dashboard$path" -Body $body }
function DDel  ($path)       { Invoke-RestMethod -SkipCertificateCheck -WebSession $sess -Headers $hdr -Method Delete -Uri "$Dashboard$path" }
function Delivered          { [int]((DGet '/api/stats').delivered) }

function Wait-Delivered([int]$baseline, [int]$timeoutSec = 30) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ((Delivered) -gt $baseline) { return }
        Start-Sleep -Milliseconds 700
    }
    throw "delivered count did not increase from $baseline within ${timeoutSec}s"
}

# Opens an SMTP session and upgrades it with STARTTLS (accepting the self-signed cert), returning readers
# over the TLS stream ready for MAIL FROM / AUTH. Throws if STARTTLS is refused.
function New-StartTlsSmtp([int]$port) {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $tcp.Connect('127.0.0.1', $port)
    $raw = $tcp.GetStream(); $raw.ReadTimeout = 8000; $raw.WriteTimeout = 8000
    $r = New-Object System.IO.StreamReader($raw)
    $w = New-Object System.IO.StreamWriter($raw); $w.NewLine = "`r`n"; $w.AutoFlush = $true
    [void]$r.ReadLine()                                                   # 220 banner
    $w.WriteLine('EHLO smoke.ci'); do { $l = $r.ReadLine() } while ($l -match '^250-')
    $w.WriteLine('STARTTLS'); $resp = $r.ReadLine()
    if ($resp -notmatch '^220') { throw "STARTTLS refused: $resp" }
    $ssl = New-Object System.Net.Security.SslStream($raw, $false, ([System.Net.Security.RemoteCertificateValidationCallback] { $true }))
    $ssl.AuthenticateAsClient('localhost')
    $sr = New-Object System.IO.StreamReader($ssl)
    $sw = New-Object System.IO.StreamWriter($ssl); $sw.NewLine = "`r`n"; $sw.AutoFlush = $true
    $sw.WriteLine('EHLO smoke.ci'); do { $l = $sr.ReadLine() } while ($l -match '^250-')   # caps over TLS
    return [pscustomobject]@{ Tcp = $tcp; R = $sr; W = $sw }
}

# --- auth: first-run set password, otherwise log in ----------------------------------------------
$status = DGet '/api/auth/status'
$pwBody = "{""password"":""$Password""}"
if ($status.needsSetup) { DPost '/api/auth/password' $pwBody | Out-Null; Write-Host 'auth: first-run password set' }
else { DPost '/api/auth/login' $pwBody | Out-Null; Write-Host 'auth: logged in' }

# --- ensure a Local default relay (captures locally, logs Delivered; no external delivery) --------
$relay = DPost '/api/relays' '{"name":"smoke-local","provider":"Local"}'
DPost "/api/relays/$($relay.id)/set-default" '{}' | Out-Null
Write-Host "relay: Local relay $($relay.id) is now the default"

# --- discover the bound SMTP port ----------------------------------------------------------------
$health = Invoke-RestMethod -SkipCertificateCheck -Uri "$Dashboard/health"
$smtpPort = 25
if ($health.smtp.listeningPorts) { $smtpPort = [int]$health.smtp.listeningPorts[0] }
elseif ($health.smtp.ports)      { $smtpPort = [int]$health.smtp.ports[0] }
Write-Host "smtp port: $smtpPort"

# === 1. API delivery ============================================================================
$key  = DPost '/api/keys' '{"name":"smoke-api","rateLimitPerMinute":0}'
$base = Delivered
$send = Invoke-RestMethod -Method Post -Uri "$Api/api/v1/messages" -Headers @{ Authorization = "Bearer $($key.key)" } `
    -ContentType 'application/json' -Body '{"from":"smoke-api@local.test","to":["dest@local.test"],"subject":"smoke-api","text":"hi"}'
if (-not $send.id) { throw "API send did not return a spool id" }
Wait-Delivered $base
Write-Host "OK: API message delivered ($($send.id))"

# === 2. SMTP delivery over STARTTLS =============================================================
$base = Delivered
$c = New-StartTlsSmtp $smtpPort
$c.W.WriteLine('MAIL FROM:<smoke-smtp@local.test>'); $m = $c.R.ReadLine(); if ($m -notmatch '^250') { throw "MAIL FROM: $m" }
$c.W.WriteLine('RCPT TO:<dest@local.test>');         $m = $c.R.ReadLine(); if ($m -notmatch '^250') { throw "RCPT TO: $m" }
$c.W.WriteLine('DATA');                               $m = $c.R.ReadLine(); if ($m -notmatch '^3')   { throw "DATA: $m" }
$c.W.WriteLine('Subject: smoke-smtp'); $c.W.WriteLine(''); $c.W.WriteLine('hello over starttls'); $c.W.WriteLine('.')
$m = $c.R.ReadLine(); if ($m -notmatch '^250') { throw "end-of-DATA: $m" }
$c.W.WriteLine('QUIT'); $c.Tcp.Close()
Wait-Delivered $base
Write-Host "OK: SMTP message delivered over STARTTLS"

# === 3. API key revocation ======================================================================
Invoke-RestMethod -Method Post -Uri "$Api/api/v1/messages" -Headers @{ Authorization = "Bearer $($key.key)" } `
    -ContentType 'application/json' -Body '{"from":"smoke-api@local.test","to":["dest@local.test"],"subject":"pre-revoke","text":"hi"}' | Out-Null
DDel "/api/keys/$($key.id)" | Out-Null
Start-Sleep -Seconds 1
$code = 0
try {
    Invoke-RestMethod -Method Post -Uri "$Api/api/v1/messages" -Headers @{ Authorization = "Bearer $($key.key)" } `
        -ContentType 'application/json' -Body '{"from":"smoke-api@local.test","to":["dest@local.test"],"subject":"post-revoke","text":"hi"}' | Out-Null
}
catch { $code = [int]$_.Exception.Response.StatusCode }
if ($code -ne 401 -and $code -ne 403) { throw "a revoked API key must be refused (401/403), got $code" }
Write-Host "OK: revoked API key refused ($code)"

# === 4. SMTP AUTH over STARTTLS =================================================================
$u = 'smoke-auth'; $cp = 'Zx9-Auth-Smoke-7q'
DPost '/api/smtp-credentials' "{""username"":""$u"",""password"":""$cp""}" | Out-Null
try {
    $c = New-StartTlsSmtp $smtpPort
    $ok = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("`0$u`0$cp"))
    $c.W.WriteLine("AUTH PLAIN $ok"); $m = $c.R.ReadLine(); $c.Tcp.Close()
    if ($m -notmatch '^235') { throw "valid AUTH should return 235, got: $m" }
    Write-Host "OK: SMTP AUTH over STARTTLS accepted valid credentials (235)"

    $c = New-StartTlsSmtp $smtpPort
    $bad = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("`0$u`0wrong-password"))
    $c.W.WriteLine("AUTH PLAIN $bad"); $m = $c.R.ReadLine(); $c.Tcp.Close()
    if ($m -notmatch '^535') { throw "bad AUTH should return 535, got: $m" }
    Write-Host "OK: SMTP AUTH over STARTTLS rejected bad credentials (535)"
}
finally { DDel "/api/smtp-credentials/$u" | Out-Null }

Write-Host "FUNCTIONAL SMOKE PASSED"
