$ErrorActionPreference = 'Stop'
# Dispatch SMTP Relay — SQL Server Express bootstrap (adapted from the FluxDeploy installer pattern).
# Downloads + silently installs SQL Server Express as a named instance, then creates the DispatchLog
# database and grants the service account (LocalSystem) sysadmin so the service can connect with
# Windows auth. Idempotent: skips install if the instance already exists. Invoked by the WiX bundle
# (via InstallSqlExpress.exe) before the Dispatch MSI, or run standalone.
$logFile = 'C:\Windows\Temp\Dispatch-SqlInstall.log'
$dbName = if ($args[0]) { $args[0] } else { 'DispatchLog' }
$instance = 'DISPATCHSQL'
$serviceName = "MSSQL`$$instance"

function Log($msg) { "$(Get-Date -Format o)  $msg" | Out-File -Append -FilePath $logFile -Encoding utf8 }

Log '=== Dispatch SQL Server Express install started ==='
Log "Instance: $instance  Database: $dbName  User: $([Environment]::UserName)"

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Log 'SQL Server Express instance already installed'
} else {
    $tempDir = 'C:\Windows\Temp'
    $bootstrapper = Join-Path $tempDir 'SQLEXPR-SSEI.exe'
    $mediaDir = Join-Path $tempDir 'DispatchSqlMedia'
    $extractDir = Join-Path $tempDir 'DispatchSqlSetup'

    Log 'Downloading SQL Server Express bootstrapper...'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/?linkid=2216019' -OutFile $bootstrapper -UseBasicParsing
    Log "Bootstrapper: $([math]::Round((Get-Item $bootstrapper).Length / 1MB, 1)) MB"

    Log 'Downloading SQL Server Express media...'
    New-Item -Path $mediaDir -ItemType Directory -Force | Out-Null
    $dlJob = Start-Process -FilePath $bootstrapper -ArgumentList '/ACTION=Download',"/MEDIAPATH=$mediaDir",'/MEDIATYPE=Core','/QUIET' -Wait -PassThru -WindowStyle Hidden
    Log "Bootstrapper exit: $($dlJob.ExitCode)"
    $mediaExe = Get-ChildItem $mediaDir -Filter 'SQLEXPR*.exe' -EA SilentlyContinue | Select-Object -First 1
    if (-not $mediaExe) { Log 'ERROR: media download failed'; exit 1 }
    Log "Media: $($mediaExe.Name) ($([math]::Round($mediaExe.Length / 1MB)) MB)"

    Log 'Extracting...'
    New-Item -Path $extractDir -ItemType Directory -Force | Out-Null
    $p = Start-Process -FilePath $mediaExe.FullName -ArgumentList '/q',"/x:$extractDir" -Wait -PassThru -WindowStyle Hidden
    Log "Extract exit: $($p.ExitCode)"
    $setupExe = Get-ChildItem $extractDir -Filter 'SETUP.EXE' -Recurse -EA SilentlyContinue | Select-Object -First 1
    if (-not $setupExe) { Log 'ERROR: SETUP.EXE not found'; exit 1 }

    # SQL 2025 changed the extraction layout — make sure MediaInfo.xml is two levels above SETUP.EXE.
    $expectedMediaInfo = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($setupExe.DirectoryName, '..', '..', 'MediaInfo.xml'))
    if (-not (Test-Path $expectedMediaInfo)) {
        $foundMediaInfo = Get-ChildItem $extractDir -Filter 'MediaInfo.xml' -Recurse -EA SilentlyContinue | Select-Object -First 1
        if ($foundMediaInfo) { Copy-Item $foundMediaInfo.FullName $expectedMediaInfo -Force; Log "Placed MediaInfo.xml at $expectedMediaInfo" }
        else { Log "WARNING: MediaInfo.xml not found in $extractDir" }
    }

    try { Remove-Item 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\170\ConfigurationState' -Recurse -Force -EA SilentlyContinue } catch {}
    try { Remove-Item 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\160\ConfigurationState' -Recurse -Force -EA SilentlyContinue } catch {}

    Log 'Installing SQL Server Express...'
    # Grant BUILTIN\ADMINISTRATORS and NT AUTHORITY\SYSTEM sysadmin so the Dispatch service (LocalSystem)
    # can connect with Windows auth. TCP disabled — the service connects over shared memory locally.
    $installArgs = '/Q /IACCEPTSQLSERVERLICENSETERMS /ACTION=Install /FEATURES=SQLENGINE ' +
        "/INSTANCENAME=$instance " +
        '/SQLSVCACCOUNT="NT AUTHORITY\SYSTEM" ' +
        '/SQLSYSADMINACCOUNTS="BUILTIN\ADMINISTRATORS" "NT AUTHORITY\SYSTEM" ' +
        '/TCPENABLED=0 /UPDATEENABLED=False /USEMICROSOFTUPDATE=False'
    Log "Args: $installArgs"
    $installProc = Start-Process -FilePath $setupExe.FullName -ArgumentList $installArgs -Wait -PassThru -WindowStyle Hidden
    Log "SETUP.EXE exit code: $($installProc.ExitCode)"
    if ($installProc.ExitCode -ne 0) {
        Log "ERROR: SQL Server Express install failed ($($installProc.ExitCode))"
        $summary = Get-ChildItem 'C:\Program Files\Microsoft SQL Server' -Filter 'Summary*.txt' -Recurse -EA SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($summary) { Get-Content $summary.FullName | Select-Object -First 20 | Out-File -Append $logFile -Encoding utf8 }
        exit $installProc.ExitCode
    }

    Remove-Item $bootstrapper -Force -EA SilentlyContinue
    Remove-Item $mediaDir -Recurse -Force -EA SilentlyContinue
    Remove-Item $extractDir -Recurse -Force -EA SilentlyContinue
}

Log 'Waiting for SQL Server Express service...'
for ($i = 0; $i -lt 120; $i++) {
    if (Get-Service -Name $serviceName -EA SilentlyContinue | Where-Object { $_.Status -eq 'Running' }) { Log 'SQL Express running'; break }
    Start-Sleep -Seconds 5
}

Log "Creating database [$dbName] + ensuring NT AUTHORITY\SYSTEM login..."
for ($attempt = 1; $attempt -le 6; $attempt++) {
    try {
        $conn = New-Object System.Data.SqlClient.SqlConnection("Server=.\$instance;Database=master;Trusted_Connection=yes;")
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = @"
IF DB_ID('$dbName') IS NULL CREATE DATABASE [$dbName];
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = 'NT AUTHORITY\SYSTEM')
    CREATE LOGIN [NT AUTHORITY\SYSTEM] FROM WINDOWS;
EXEC sp_addsrvrolemember 'NT AUTHORITY\SYSTEM', 'sysadmin';
"@
        $cmd.ExecuteNonQuery() | Out-Null
        $conn.Close()
        Log "Database [$dbName] ready"
        break
    } catch {
        Log "DB attempt $attempt/6: $($_.Exception.Message)"
        Start-Sleep -Seconds 10
    }
}

Log '=== Dispatch SQL Server Express install complete ==='
exit 0
