#Requires -Version 7
<#
  Functional QC smoke for a real Dispatch install - the pre-release gate that proves core functionality
  actually works end to end (not just that the service starts). Uses the Local provider so nothing leaves
  the box and delivery is deterministic; delivery is confirmed via the /stats delivered counter (the log
  stores spool ids in a different format than the API returns, so a counter delta is the robust signal).

  Coverage (each section cleans up the resources it creates):

    1.  API delivery            - POST /api/v1/messages (HTTP, port 8025) -> Delivered via the Local relay
    2.  SMTP delivery           - a real SMTP session upgraded with STARTTLS -> Delivered
    3.  API key revocation      - a revoked key is refused (401)
    4.  SMTP AUTH               - over STARTTLS: valid creds accepted (235), bad creds rejected (535)
    5.  Local Inbox + features  - API message with cc/html/tags is captured; detail shows cc + bodies
    6.  Attachments (SMTP)      - a MIME attachment sent over SMTP is parsed and downloadable from the inbox
    6b. Attachments (HTTP API)  - a Mailgun-style multipart 'attachment' upload is parsed and downloadable
    7.  Routing rules + simulate - a recipient rule routes to the chosen relay; non-match falls to default
    8.  Relay test endpoint     - POST /relays/{id}/test succeeds against the Local provider
    9.  Reports round-trip      - /reports reflects the deliveries we just made
    10. Config round-trip       - PUT /config/api persists + reloads the cache (live setting)
    11. Settings round-trip     - PUT /settings persists a retention threshold
    12. Audit log               - operations produce audit rows
    13. Retention purges        - real data: backdated files are deleted at 1-day retention and KEPT at
                                  0 (0 = keep forever); log/audit purges honour 0 too; recorded in history
    14. Storage usage           - /storage breakdown reflects real rows/files (per-event + spool dirs)
    15. Read-only endpoints      - health/stats/system/spool/metrics all answer
    16. Size-pressure archive    - forces Express size-pressure; oldest rows exported to weekly JSONL
                                  before deletion (destructive, runs last)

  Run after the service is up (serving /health).
#>
[CmdletBinding()]
param(
    [string]$Dashboard = 'https://localhost:8420',
    [string]$Api       = 'http://localhost:8025',
    [string]$Password  = 'Zq7-Marsh-Pylon-Vex!',
    [string]$DataRoot  = "$env:ProgramData\Dispatch"   # content root; relative spool/key paths resolve here
)
$ErrorActionPreference = 'Stop'
$hdr  = @{ 'X-Dispatch-Request' = '1' }
$sess = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$tok  = (New-Guid).Guid.Substring(0, 8)   # run-unique token so subjects don't collide across runs

function DGet  ($path)       { Invoke-RestMethod -SkipCertificateCheck -WebSession $sess -Uri "$Dashboard$path" }
function DPost ($path,$body) { Invoke-RestMethod -SkipCertificateCheck -WebSession $sess -Headers $hdr -ContentType 'application/json' -Method Post -Uri "$Dashboard$path" -Body $body }
function DPut  ($path,$body) { Invoke-RestMethod -SkipCertificateCheck -WebSession $sess -Headers $hdr -ContentType 'application/json' -Method Put -Uri "$Dashboard$path" -Body $body }
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

# Runs a full MAIL/RCPT/DATA exchange over an open (post-STARTTLS) connection. $dataLines are the raw
# message lines (headers + blank line + body); the trailing "." is appended here.
function Send-SmtpMessage($c, [string]$from, [string]$to, [string[]]$dataLines) {
    $c.W.WriteLine("MAIL FROM:<$from>"); $m = $c.R.ReadLine(); if ($m -notmatch '^250') { throw "MAIL FROM: $m" }
    $c.W.WriteLine("RCPT TO:<$to>");     $m = $c.R.ReadLine(); if ($m -notmatch '^250') { throw "RCPT TO: $m" }
    $c.W.WriteLine('DATA');              $m = $c.R.ReadLine(); if ($m -notmatch '^3')   { throw "DATA: $m" }
    foreach ($line in $dataLines) { $c.W.WriteLine($line) }
    $c.W.WriteLine('.'); $m = $c.R.ReadLine(); if ($m -notmatch '^250') { throw "end-of-DATA: $m" }
}

# Returns an Invoke-WebRequest body as text whether PowerShell decoded it as a string (text content
# types, e.g. text/plain) or left it as raw bytes (binary content types, e.g. application/octet-stream).
function Get-BodyText($resp) {
    if ($resp.Content -is [byte[]]) { return [Text.Encoding]::ASCII.GetString($resp.Content) }
    return [string]$resp.Content
}

# Polls the Local Inbox for a captured message with the given subject and returns its id (.eml name).
function Find-LocalMessageId([string]$subject, [int]$timeoutSec = 15) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $hit = (DGet '/api/local/messages').items | Where-Object { $_.subject -eq $subject } | Select-Object -First 1
        if ($hit) { return $hit.id }
        Start-Sleep -Milliseconds 500
    }
    throw "local message '$subject' not captured within ${timeoutSec}s"
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
Send-SmtpMessage $c 'smoke-smtp@local.test' 'dest@local.test' @('Subject: smoke-smtp', '', 'hello over starttls')
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

# === 5. Local Inbox + message features (cc / html / tags via the HTTP API) =======================
DDel '/api/local/messages' | Out-Null                          # start from a clean inbox for a precise assertion
$key2 = DPost '/api/keys' '{"name":"smoke-features","rateLimitPerMinute":0}'
$subj = "smoke-features-$tok"
$base = Delivered
$body = @{
    from = 'features@local.test'; to = @('dest@local.test'); cc = @('carbon@local.test')
    subject = $subj; text = 'plain part'; html = '<p>html part</p>'
    headers = @{ 'X-Smoke-Tag' = $tok }; tags = @('smoke', 'features')
} | ConvertTo-Json -Depth 5
Invoke-RestMethod -Method Post -Uri "$Api/api/v1/messages" -Headers @{ Authorization = "Bearer $($key2.key)" } `
    -ContentType 'application/json' -Body $body | Out-Null
Wait-Delivered $base
$id = Find-LocalMessageId $subj
$det = DGet "/api/local/messages/$id"
if ($det.cc -notmatch 'carbon@local.test') { throw "captured message is missing the Cc recipient: $($det.cc)" }
if ($det.html -notmatch 'html part')       { throw "captured message is missing the HTML body" }
if ($det.text -notmatch 'plain part')      { throw "captured message is missing the text body" }
DDel "/api/keys/$($key2.id)" | Out-Null
Write-Host "OK: Local Inbox captured a feature-rich message (cc + text + html)"

# === 6. Attachments: send a MIME attachment over SMTP, parse + download it from the inbox =========
$asubj = "smoke-attach-$tok"
$attText = "dispatch-smoke-attachment-$tok"
$attB64 = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($attText))
$base = Delivered
$c = New-StartTlsSmtp $smtpPort
Send-SmtpMessage $c 'attach@local.test' 'dest@local.test' @(
    "Subject: $asubj", 'MIME-Version: 1.0', 'Content-Type: multipart/mixed; boundary="bnd1"', '',
    '--bnd1', 'Content-Type: text/plain; charset=utf-8', '', 'see attached', '',
    '--bnd1', 'Content-Type: application/octet-stream; name="hello.txt"',
    'Content-Disposition: attachment; filename="hello.txt"', 'Content-Transfer-Encoding: base64', '',
    $attB64, '', '--bnd1--')
$c.W.WriteLine('QUIT'); $c.Tcp.Close()
Wait-Delivered $base
$aid = Find-LocalMessageId $asubj
$adet = DGet "/api/local/messages/$aid"
if (@($adet.attachments).Count -lt 1) { throw "attachment was not parsed from the captured message" }
$dl = Invoke-WebRequest -SkipCertificateCheck -WebSession $sess -Uri "$Dashboard/api/local/messages/$aid/attachments/0"
if ((Get-BodyText $dl) -notmatch [regex]::Escape($attText)) { throw "downloaded attachment content did not match" }
Write-Host "OK: SMTP attachment parsed by the inbox and downloaded intact"

# === 6b. Attachments over the HTTP API (Mailgun-style multipart 'attachment' field) ==============
$apiSubj    = "smoke-attach-api-$tok"
$apiAttText = "dispatch-api-attachment-$tok"
$attFile    = Join-Path ([IO.Path]::GetTempPath()) "smoke-attach-$tok.txt"
Set-Content -LiteralPath $attFile -Value $apiAttText -NoNewline -Encoding ascii
$key3 = DPost '/api/keys' '{"name":"smoke-attach-api","rateLimitPerMinute":0}'
$base = Delivered
try {
    # -Form sends multipart/form-data; a FileInfo value becomes a file part. This is the exact Mailgun shape:
    # `attachment` file field(s), `o:tag` tags, `h:<Name>` custom headers.
    $form = @{
        from = 'attach-api@local.test'; to = 'dest@local.test'; subject = $apiSubj
        text = 'see api attachment'; 'o:tag' = 'smoke'; attachment = Get-Item -LiteralPath $attFile
    }
    Invoke-RestMethod -Method Post -Uri "$Api/api/v1/messages" -Headers @{ Authorization = "Bearer $($key3.key)" } -Form $form | Out-Null
    Wait-Delivered $base
    $apiId  = Find-LocalMessageId $apiSubj
    $apiDet = DGet "/api/local/messages/$apiId"
    if (@($apiDet.attachments).Count -lt 1) { throw "API multipart attachment was not parsed into the message" }
    $apiDl  = Invoke-WebRequest -SkipCertificateCheck -WebSession $sess -Uri "$Dashboard/api/local/messages/$apiId/attachments/0"
    if ((Get-BodyText $apiDl) -notmatch [regex]::Escape($apiAttText)) { throw "downloaded API attachment content did not match" }
    Write-Host "OK: HTTP API accepted a multipart attachment (Mailgun-style) - parsed + downloaded intact"
}
finally {
    DDel "/api/keys/$($key3.id)" | Out-Null
    Remove-Item -LiteralPath $attFile -ErrorAction SilentlyContinue
}

# === 7. Routing rules + simulate ================================================================
# Routing rules match on the recipient DOMAIN (exact / *.domain / *), so the rule + simulate use a
# unique domain rather than a local-part pattern.
$routeDomain = "route-$tok.test"
$r2 = DPost '/api/relays' '{"name":"smoke-route","provider":"Local"}'
$rule = DPost '/api/routing/rules' "{""name"":""smoke-rule-$tok"",""recipientPattern"":""$routeDomain"",""relayId"":$($r2.id),""priority"":100}"
try {
    $hit = DPost '/api/routing/simulate' "{""from"":""s@local.test"",""to"":""user@$routeDomain""}"
    if ([int]$hit.relayId -ne [int]$r2.id) { throw "rule should route to relay $($r2.id), simulate chose $($hit.relayId)" }
    if (-not $hit.matched)                 { throw "simulate should report matched=true for the rule recipient" }
    $miss = DPost '/api/routing/simulate' '{"from":"s@local.test","to":"someone-else@unmatched.test"}'
    if ([int]$miss.relayId -ne [int]$relay.id) { throw "non-matching recipient should fall to the default relay $($relay.id), got $($miss.relayId)" }
    Write-Host "OK: routing rule matched in simulate; non-match fell through to the default relay"
}
finally {
    DDel "/api/routing/rules/$($rule.id)" | Out-Null
    DDel "/api/relays/$($r2.id)" | Out-Null
}

# === 8. Relay test endpoint (Local provider) ====================================================
$test = DPost "/api/relays/$($relay.id)/test" '{"to":"relay-test@local.test"}'
if (-not $test.ok) { throw "relay test against the Local provider should succeed: $($test | ConvertTo-Json -Compress)" }
Write-Host "OK: relay test endpoint succeeded against the Local provider"

# === 9. Reports round-trip ======================================================================
$rep = DGet '/api/reports'
if ([int]$rep.summary.received -lt 1)  { throw "reports should show received >= 1, got $($rep.summary.received)" }
if ([int]$rep.summary.delivered -lt 1) { throw "reports should show delivered >= 1, got $($rep.summary.delivered)" }
Write-Host "OK: reports reflect deliveries (received=$($rep.summary.received), delivered=$($rep.summary.delivered))"

# === 10. Config round-trip (live-applied: API rate limit persists + cache reloads) ===============
$origRl = [int](DGet '/api/config').api.rateLimitPerKey
$newRl  = $origRl + 17
DPut '/api/config/api' "{""rateLimitPerKey"":$newRl}" | Out-Null
$readRl = [int](DGet '/api/config').api.rateLimitPerKey
DPut '/api/config/api' "{""rateLimitPerKey"":$origRl}" | Out-Null     # restore
if ($readRl -ne $newRl) { throw "config change did not persist/reload: expected $newRl, read $readRl" }
Write-Host "OK: config round-trip persisted and reloaded (rateLimitPerKey $origRl -> $newRl -> restored)"

# === 11. Settings round-trip (retention threshold persists) =====================================
$origDays = [int](DGet '/api/settings').retention.logDeliveredRetentionDays
$newDays  = $origDays + 1
DPut '/api/settings' "{""retention"":{""logDeliveredRetentionDays"":$newDays}}" | Out-Null
$readDays = [int](DGet '/api/settings').retention.logDeliveredRetentionDays
DPut '/api/settings' "{""retention"":{""logDeliveredRetentionDays"":$origDays}}" | Out-Null   # restore
if ($readDays -ne $newDays) { throw "settings change did not persist: expected $newDays, read $readDays" }
Write-Host "OK: settings round-trip persisted (logDeliveredRetentionDays $origDays -> $newDays -> restored)"

# === 12. Audit log (login + config changes produced rows; also exercise the category filter) =======
$audit = DGet '/api/audit'
if (@($audit.rows).Count -lt 1) { throw "audit log should contain rows after login + config changes" }
$cfgAudit = DGet '/api/audit?category=Config'   # config PUTs above audit with category "Config"
if (@($cfgAudit.rows).Count -lt 1) { throw "audit log should contain Config-category rows after config changes" }
Write-Host "OK: audit log recorded operations ($(@($audit.rows).Count) rows, $(@($cfgAudit.rows).Count) Config)"

# === 13. Retention purges actually delete - and 0 means "keep forever" (real data, live install) ==
# Verified against real spooled files on the box: backdate a file so a 1-day retention removes it, and
# confirm 0 keeps it (the industry "0 = keep forever" convention). The purge worker reads retention via
# a 10-second cache, so each change waits the TTL out before the dependent purge. Settings are restored
# afterwards so the install is never left with weakened retention.

# PUT a retention change, wait out the purge-settings cache, then run a manual purge.
function Invoke-PurgeAfter($retentionJson) {
    DPut '/api/settings' $retentionJson | Out-Null
    Start-Sleep -Seconds 12
    DPost '/api/purge/run' '{}' | Out-Null
}

# Writes a dummy .eml into $dir and backdates it 10 days so a >=1-day retention will purge it.
function New-AgedEml([string]$dir, [string]$name) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $p = Join-Path $dir $name
    Set-Content -LiteralPath $p -Value "From: aged@local.test`r`nSubject: aged`r`n`r`nbody" -Encoding ascii
    (Get-Item -LiteralPath $p).LastWriteTimeUtc = (Get-Date).ToUniversalTime().AddDays(-10)
    return $p
}

$origRet  = (DGet '/api/settings').retention
$spoolDir = (DGet '/api/config').spool.directory
# The service stores a relative spool dir (e.g. ./.dispatch-spool) resolved against its content root
# (the ProgramData data dir), so make it absolute before touching the filesystem.
if ($spoolDir -and -not [System.IO.Path]::IsPathRooted($spoolDir)) {
    $spoolDir = Join-Path $DataRoot ($spoolDir -replace '^\.[\\/]', '')
}
if ($spoolDir -and (Test-Path -LiteralPath $spoolDir)) {
    # 13a. Captured / Local Inbox file purge - 0 keeps, 1 deletes the aged file.
    $capEml = New-AgedEml (Join-Path $spoolDir 'captured') "smoke-purge-cap-$tok.eml"
    Invoke-PurgeAfter '{"retention":{"capturedRetentionDays":0}}'
    if (-not (Test-Path -LiteralPath $capEml)) { throw "capturedRetentionDays=0 must KEEP files (0 = keep forever), but the aged file was deleted" }
    Invoke-PurgeAfter '{"retention":{"capturedRetentionDays":1}}'
    if (Test-Path -LiteralPath $capEml) { Remove-Item -LiteralPath $capEml -Force -EA SilentlyContinue; throw "capturedRetentionDays=1 should have deleted the 10-day-old captured file" }
    Write-Host "OK: captured purge - 0 keeps the aged file, 1 deletes it"

    # 13b. Spool failed-file purge - same 0-keeps / 1-deletes proof on real spool files.
    $failEml = New-AgedEml (Join-Path $spoolDir 'failed') "smoke-purge-fail-$tok.eml"
    Invoke-PurgeAfter '{"retention":{"spoolFailedRetentionDays":0}}'
    if (-not (Test-Path -LiteralPath $failEml)) { throw "spoolFailedRetentionDays=0 must KEEP files, but the aged file was deleted" }
    Invoke-PurgeAfter '{"retention":{"spoolFailedRetentionDays":1}}'
    if (Test-Path -LiteralPath $failEml) { Remove-Item -LiteralPath $failEml -Force -EA SilentlyContinue; throw "spoolFailedRetentionDays=1 should have deleted the 10-day-old failed file" }
    Write-Host "OK: spool failed-file purge - 0 keeps the aged file, 1 deletes it"
}
else { Write-Host "SKIP: spool dir not locally accessible ($spoolDir) - file-purge deletion not exercised" }

# 13c. Log-row + audit purge honour 0 = keep forever. (A fresh install has no >1-day-old rows to
#      age-delete, so we prove the guard the user emphasized: retention 0 must NOT delete current data.)
$delBefore = [int](DGet '/api/messages?event=Delivered&pageSize=1').total
if ($delBefore -lt 1) { throw "expected Delivered log rows from the deliveries above" }
Invoke-PurgeAfter '{"retention":{"logDeliveredRetentionDays":0,"auditRetentionDays":0,"auditSecurityRetentionDays":0}}'
$delAfter = [int](DGet '/api/messages?event=Delivered&pageSize=1').total
if ($delAfter -lt $delBefore) { throw "logDeliveredRetentionDays=0 must keep all rows (0 = keep forever), but Delivered rows dropped ($delBefore -> $delAfter)" }
Write-Host "OK: log + audit purge honour 0 = keep forever (Delivered rows held at $delAfter)"

# restore the original retention thresholds so the install isn't left with weakened retention.
$restore = @{ retention = @{
    capturedRetentionDays      = [int]$origRet.capturedRetentionDays
    spoolFailedRetentionDays   = [int]$origRet.spoolFailedRetentionDays
    logDeliveredRetentionDays  = [int]$origRet.logDeliveredRetentionDays
    auditRetentionDays         = [int]$origRet.auditRetentionDays
    auditSecurityRetentionDays = [int]$origRet.auditSecurityRetentionDays
} } | ConvertTo-Json -Depth 5
DPut '/api/settings' $restore | Out-Null
if (@(DGet '/api/purge/history').Count -lt 1) { throw "a manual purge should appear in purge history" }
Write-Host "OK: purges recorded in history; retention thresholds restored"

# === 14. Storage usage breakdown reflects real data =============================================
$st = DGet '/api/storage'
if (-not $st.database.connected) { throw "storage: database should report connected" }
$deliveredUse = $st.database.relayLog.byEvent | Where-Object { $_.event -eq 'Delivered' } | Select-Object -First 1
if (-not $deliveredUse -or [int]$deliveredUse.rows -lt 1) { throw "storage: expected Delivered rows in the message-log breakdown" }
if ([long]$st.database.totalBytes -le 0) { throw "storage: database total size should be > 0" }
# Every backend must report a real per-table size, including the bundled SQLite default - a storage page
# that shows 0 KB for the message log is telling the operator something false about their own system.
if ([long]$st.database.relayLog.tableBytes -le 0) { throw "storage: relay_log table size should be > 0" }
if ([long]$st.database.relayLog.tableBytes -gt [long]$st.database.totalBytes) {
    throw "storage: relay_log cannot be larger than the database containing it"
}
if ($null -eq $st.spool.captured.files) { throw "storage: spool.captured.files missing" }
if ($null -eq $st.spool.failed.bytes)   { throw "storage: spool.failed.bytes missing" }
Write-Host "OK: storage usage - Delivered rows=$($deliveredUse.rows), relay_log=$([math]::Round($st.database.relayLog.tableBytes/1KB))KB of $([math]::Round($st.database.totalBytes/1KB))KB, captured files=$($st.spool.captured.files)"

# === 15. Read-only / observability endpoints all answer =========================================
foreach ($p in '/api/stats', '/api/stats/relays', '/api/stats/throughput', '/api/system', '/api/spool', '/health') {
    DGet $p | Out-Null
}
$metrics = Invoke-WebRequest -SkipCertificateCheck -WebSession $sess -Uri "$Dashboard/metrics"
if ([int]$metrics.StatusCode -ne 200) { throw "/metrics did not return 200 (got $($metrics.StatusCode))" }
Write-Host "OK: stats / system / spool / health / metrics all answer"

# === 16. Size-pressure ARCHIVES then deletes (opt-in DB-size cap) - DESTRUCTIVE, runs last ========
# Force size-pressure by setting the (normally-off) trigger below current usage, then confirm the oldest rows are
# exported to weekly JSONL *before* being deleted (the emergency purge never silently loses history).
# This wipes the message-log/audit on the CI box, so it is intentionally the final step.
if ($spoolDir -and (Test-Path -LiteralPath $spoolDir)) {
    $archiveDir = Join-Path $spoolDir 'archive'
    Get-ChildItem -LiteralPath $archiveDir -Filter *.jsonl -EA SilentlyContinue | Remove-Item -Force -EA SilentlyContinue
    $rowsBefore = [int](DGet '/api/messages?event=Delivered&pageSize=1').total
    Invoke-PurgeAfter '{"retention":{"sizeTriggerGb":0.001,"sizeTargetGb":0.0005}}'   # ~1 MB trigger -> always over
    Start-Sleep -Seconds 2
    $archives = @(Get-ChildItem -LiteralPath $archiveDir -Filter 'relay_log-*.jsonl' -EA SilentlyContinue)
    if ($archives.Count -lt 1) { throw "size-pressure should have written a relay_log JSONL archive before deleting" }
    if ((Get-Content -LiteralPath $archives[0].FullName -TotalCount 1) -notmatch '"event"') { throw "archived JSONL row should carry the row fields" }
    $rowsAfter = [int](DGet '/api/messages?event=Delivered&pageSize=1').total
    if ($rowsAfter -ge $rowsBefore) { throw "size-pressure should have deleted message-log rows ($rowsBefore -> $rowsAfter)" }
    DPut '/api/settings' '{"retention":{"sizeTriggerGb":0,"sizeTargetGb":0}}' | Out-Null   # restore (0 = disabled, the default)
    Write-Host "OK: size-pressure archived $($archives.Count) JSONL file(s) then deleted rows ($rowsBefore -> $rowsAfter)"
}
else { Write-Host "SKIP: spool dir not locally accessible - size-pressure archive test not exercised" }

Write-Host "FUNCTIONAL SMOKE PASSED"
