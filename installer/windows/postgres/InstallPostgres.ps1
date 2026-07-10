$ErrorActionPreference = 'Stop'
# Dispatch SMTP Relay - PostgreSQL bootstrap.
# Downloads + silently installs PostgreSQL as a dedicated Windows service, then creates the DispatchLog
# database and a least-privilege 'dispatch' role that owns it. Local (loopback) connections for that role
# are trusted in pg_hba.conf, so the Dispatch service connects with no password in appsettings.json -
# mirroring the old password-less Windows-auth model, while the server only listens on localhost.
# Idempotent: skips install if the service already exists. Invoked by the WiX bundle (via
# InstallPostgres.exe) before the Dispatch MSI, or run standalone.
$logFile = 'C:\Windows\Temp\Dispatch-PostgresInstall.log'
$dbName = if ($args[0]) { $args[0] } else { 'DispatchLog' }
$role = 'dispatch'
$serviceName = 'DispatchPostgres'
$port = 5432
$installDir = 'C:\Program Files\Dispatch\PostgreSQL'
$dataDir = 'C:\ProgramData\Dispatch\pgdata'

# Pinned PostgreSQL 17 Windows installer (EDB). Verify the SHA-256 before running, so a tampered or
# truncated download can never be executed. Update both when bumping the pinned version.
$installerUrl = 'https://get.enterprisedb.com/postgresql/postgresql-17.2-1-windows-x64.exe'
$installerSha256 = 'REPLACE_WITH_PINNED_SHA256'   # set at release time; placeholder skips the check with a warning

function Log($msg) { "$(Get-Date -Format o)  $msg" | Out-File -Append -FilePath $logFile -Encoding utf8 }

Log '=== Dispatch PostgreSQL install started ==='
Log "Service: $serviceName  Database: $dbName  Role: $role  Port: $port"

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Log 'PostgreSQL service already installed'
} else {
    $tempDir = 'C:\Windows\Temp'
    $installer = Join-Path $tempDir 'DispatchPostgresSetup.exe'

    Log 'Downloading PostgreSQL installer...'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $installerUrl -OutFile $installer -UseBasicParsing
    Log "Installer: $([math]::Round((Get-Item $installer).Length / 1MB, 1)) MB"

    if ($installerSha256 -and $installerSha256 -ne 'REPLACE_WITH_PINNED_SHA256') {
        $actual = (Get-FileHash -Path $installer -Algorithm SHA256).Hash
        if ($actual -ne $installerSha256.ToUpper()) {
            Log "ERROR: installer SHA-256 mismatch (expected $installerSha256, got $actual)"
            exit 1
        }
        Log 'Installer SHA-256 verified'
    } else {
        Log 'WARNING: installer SHA-256 not pinned - skipping integrity check'
    }

    # The superuser password is generated, used only to provision the role/db below, and never persisted in
    # Dispatch config (the service authenticates as the 'dispatch' role via local trust).
    Add-Type -AssemblyName System.Web
    $superPw = [System.Web.Security.Membership]::GeneratePassword(24, 4)

    Log 'Installing PostgreSQL (server + command-line tools only)...'
    $installArgs = @(
        '--mode', 'unattended',
        '--unattendedmodeui', 'none',
        '--prefix', "`"$installDir`"",
        '--datadir', "`"$dataDir`"",
        '--servicename', $serviceName,
        '--serverport', "$port",
        '--superpassword', "`"$superPw`"",
        '--enable-components', 'server,commandlinetools',
        '--disable-components', 'pgAdmin,stackbuilder'
    )
    $installProc = Start-Process -FilePath $installer -ArgumentList $installArgs -Wait -PassThru -WindowStyle Hidden
    Log "Installer exit code: $($installProc.ExitCode)"
    if ($installProc.ExitCode -ne 0) {
        Log "ERROR: PostgreSQL install failed ($($installProc.ExitCode))"
        exit $installProc.ExitCode
    }

    Remove-Item $installer -Force -EA SilentlyContinue

    $psql = Join-Path $installDir 'bin\psql.exe'
    $env:PGPASSWORD = $superPw

    Log 'Waiting for PostgreSQL to accept connections...'
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        & (Join-Path $installDir 'bin\pg_isready.exe') -h 127.0.0.1 -p $port -U postgres 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 2
    }
    if (-not $ready) { Log 'ERROR: PostgreSQL did not become ready'; exit 1 }

    Log "Creating role [$role] and database [$dbName]..."
    # Role: idempotent via a DO block (a single statement, so it is safe with psql -c). CREATE DATABASE
    # cannot run inside DO/a transaction, so guard it with a separate existence check + create.
    $roleSql = "DO `$`$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$role') THEN CREATE ROLE $role LOGIN; END IF; END `$`$;"
    & $psql -h 127.0.0.1 -p $port -U postgres -d postgres -v ON_ERROR_STOP=1 -c $roleSql 2>&1 | ForEach-Object { Log $_ }

    $dbExists = (& $psql -h 127.0.0.1 -p $port -U postgres -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$dbName'") | Out-String
    if ($dbExists.Trim() -ne '1') {
        & $psql -h 127.0.0.1 -p $port -U postgres -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE ""$dbName"" OWNER $role;" 2>&1 | ForEach-Object { Log $_ }
    }
    Remove-Item Env:\PGPASSWORD -EA SilentlyContinue

    # Restrict the server to loopback and trust the dispatch role locally (no password in Dispatch config).
    Log 'Configuring pg_hba.conf (localhost trust for the dispatch role) + listen_addresses...'
    $hba = Join-Path $dataDir 'pg_hba.conf'
    $conf = Join-Path $dataDir 'postgresql.conf'
    @(
        "# Dispatch: trust the local service role over loopback only.",
        "host    $dbName    $role    127.0.0.1/32    trust",
        "host    $dbName    $role    ::1/128         trust"
    ) | Add-Content -Path $hba -Encoding ascii
    Add-Content -Path $conf -Value "listen_addresses = 'localhost'" -Encoding ascii
    Restart-Service -Name $serviceName -Force
}

Log 'Waiting for PostgreSQL service to be running...'
for ($i = 0; $i -lt 60; $i++) {
    if (Get-Service -Name $serviceName -EA SilentlyContinue | Where-Object { $_.Status -eq 'Running' }) { Log 'PostgreSQL running'; break }
    Start-Sleep -Seconds 2
}

Log '=== Dispatch PostgreSQL install complete ==='
exit 0
