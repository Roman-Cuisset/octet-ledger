[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LegacyPackage,
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$EvidencePath
)

$ErrorActionPreference = 'Stop'
$profileDirectory = [Environment]::GetFolderPath('LocalApplicationData')
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or
    $env:RUNNER_ARCH -ne 'X64' -or $profileDirectory -ne $env:LOCALAPPDATA) {
    throw 'This test requires a disposable GitHub-hosted Windows x64 profile. The 0.3.0 collector ignores redirected profile paths.'
}
$startupKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$installDirectory = Join-Path $profileDirectory 'Programs\OctetLedger'
$dataDirectory = Join-Path $profileDirectory 'OctetLedger'
$startup = Get-ItemProperty -LiteralPath $startupKey -ErrorAction SilentlyContinue
if ((Test-Path -LiteralPath $dataDirectory) -or (Test-Path -LiteralPath $installDirectory) -or
    ($startup.PSObject.Properties.Name -like 'OctetLedger Collector*') -or
    (Get-ScheduledTask -TaskName 'OctetLedger Watchdog*' -ErrorAction SilentlyContinue)) {
    throw 'The runner profile already contains an OctetLedger installation, startup registration, or data.'
}

$evidenceFile = [IO.Path]::GetFullPath($EvidencePath)
New-Item -ItemType Directory -Path (Split-Path -Parent $evidenceFile) -Force | Out-Null
$workspace = Join-Path $env:RUNNER_TEMP ('octetledger-legacy-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workspace | Out-Null
$old = Join-Path $workspace 'legacy'
$new = Join-Path $workspace 'candidate'
$installed = Join-Path $installDirectory 'octetledger.exe'
$database = Join-Path $dataDirectory 'octetledger.db'
$settings = Join-Path $dataDirectory 'settings.json'
$ready = Join-Path $dataDirectory 'collector.ready'
$pidFile = Join-Path $dataDirectory 'collector.pid'
$originalPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$identity = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes($dataDirectory.ToUpperInvariant()))).Substring(0, 16)
$runName = "OctetLedger Collector $identity"
$taskName = "OctetLedger Watchdog $identity"
$evidence = [ordered]@{
    status = 'fail'
    commit = $env:GITHUB_SHA
    os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    steps = [Collections.Generic.List[object]]::new()
}
function Record-Step([string]$Name) {
    $evidence.steps.Add(@{ name = $Name; utc = [DateTimeOffset]::UtcNow.ToString('O') })
    Write-Host $Name
}
function Wait-For([scriptblock]$Check, [string]$Description, [int]$Seconds = 100) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $result = & $Check
        if ($result) { return $result }
        Start-Sleep -Milliseconds 500
    } while ($timer.Elapsed.TotalSeconds -lt $Seconds)
    throw "Timed out waiting for $Description."
}
function Get-Worker {
    $worker = Get-Content -LiteralPath $pidFile -Raw -ErrorAction SilentlyContinue
    if (-not $worker -or $worker -notmatch '^(\d+)(\||\s*$)') { return }
    $id = [int]$Matches[1]
    Get-CimInstance Win32_Process -Filter "ProcessId=$id" |
        Where-Object { $_.ExecutablePath -eq $installed -and $_.CommandLine -match '\smonitor\s' -and $_.CommandLine -match '--background' }
}
function Assert-Registration([bool]$Legacy) {
    $key = Get-ItemProperty -LiteralPath $startupKey
    $expected = if ($Legacy) { 'OctetLedger Collector' } else { $runName }
    $unexpected = if ($Legacy) { $runName } else { 'OctetLedger Collector' }
    $hasTask = [bool](Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)
    if (-not $key.PSObject.Properties[$expected] -or $key.$expected -notlike "*$dataDirectory\collector.vbs*" -or
        $key.PSObject.Properties[$unexpected] -or $hasTask -eq $Legacy) {
        throw "Incorrect collector registration (legacy=$Legacy)."
    }
}
function Read-Database([string]$Path, [string]$Mode) {
    $code = @'
import sqlite3, sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

path, mode = sys.argv[1:]
fixture = 'legacy-upgrade-fixture'
uri = Path(path).resolve().as_uri() + ('?mode=rw' if mode == 'seed' else '?mode=ro')
with sqlite3.connect(uri, uri=True) as db:
    assert db.execute('PRAGMA quick_check').fetchone() == ('ok',)
    if mode == 'seed':
        stamp = (datetime.now(timezone.utc) - timedelta(days=2)).strftime('%Y-%m-%dT12:00:00.0000000+00:00')
        db.execute('INSERT INTO adapter_state (interface_id,name,description,type,last_received,last_sent,last_seen_utc) VALUES (?,?,?,?,?,?,?)',
                   (fixture, fixture, 'Fixture', 'Ethernet', 8192, 2048, stamp))
        db.execute('INSERT INTO traffic_minute (interface_id,minute_utc,bytes_received,bytes_sent) VALUES (?,?,?,?)',
                   (fixture, stamp, 8192, 2048))
    if mode == 'adapter':
        if db.execute("SELECT 1 FROM sqlite_master WHERE name='adapter_state'").fetchone():
            row = db.execute('SELECT interface_id FROM adapter_state ORDER BY interface_id LIMIT 1').fetchone()
            if row:
                print(row[0])
    else:
        assert db.execute('SELECT bytes_received,bytes_sent FROM traffic_minute WHERE interface_id=?', (fixture,)).fetchall() == [(8192, 2048)]
        if mode == 'collections':
            samples = db.execute('SELECT DISTINCT captured_utc FROM collection_sample ORDER BY captured_utc').fetchall()
            assert len(samples) >= 2, samples
            print('\n'.join(row[0] for row in samples))
        else:
            print(db.execute('PRAGMA user_version').fetchone()[0])
'@
    $result = $code | python - $Path $Mode
    if ($LASTEXITCODE -ne 0) { throw "Historical fixture SQLite check failed ($Mode)." }
    return $result
}

try {
    $probe = Join-Path $workspace 'probe.vbs'
    'WScript.Echo "VBScript OK"' | Set-Content -LiteralPath $probe -Encoding Ascii
    $response = & cscript.exe //nologo $probe 2>&1
    if ($LASTEXITCODE -ne 0 -or $response -ne 'VBScript OK') {
        $evidence.status = 'blocked'
        throw "VBScript is unavailable on the isolated runner. $response"
    }
    Record-Step 'vbscript-engine-verified'

    $evidence.legacyPackageSha256 = (Get-FileHash -LiteralPath $LegacyPackage).Hash
    if ($evidence.legacyPackageSha256 -ne 'F549C7A74C6294A672B8D2157212714ECF9F4A2B527D87FA7E2EAA5F3B0FF273') {
        throw 'The legacy ZIP differs from the published v0.3.0 x64 archive.'
    }
    $evidence.candidatePackageSha256 = (Get-FileHash -LiteralPath $PackagePath).Hash
    Expand-Archive -LiteralPath $LegacyPackage -DestinationPath $old
    Expand-Archive -LiteralPath $PackagePath -DestinationPath $new
    $oldHash = (Get-FileHash -LiteralPath "$old\octetledger.exe").Hash
    $newHash = (Get-FileHash -LiteralPath "$new\octetledger.exe").Hash

    & "$old\install.ps1"
    if ((& $installed version) -ne 'OctetLedger 0.3.0' -or (Get-FileHash $installed).Hash -ne $oldHash) { throw 'Incorrect legacy installation.' }
    $firstLegacy = Wait-For {
        $worker = Get-Worker
        if ($worker -and (Test-Path -LiteralPath $database)) {
            $id = Read-Database $database 'adapter'
            if ($id) { @{ worker = $worker; adapter = $id } }
        }
    } 'automatic legacy collection'
    & $installed collector stop
    if ($LASTEXITCODE -ne 0) { throw 'Could not pause the legacy collector for fixture setup.' }
    & $installed interface set $firstLegacy.adapter
    if ($LASTEXITCODE -ne 0) { throw 'Legacy preferred interface setting failed.' }
    $settingsHash = (Get-FileHash -LiteralPath $settings).Hash
    $evidence.originalSchema = [int](Read-Database $database 'seed')
    $backup = Join-Path $workspace 'before-upgrade.db'
    & $installed database backup $backup
    if ($LASTEXITCODE -ne 0 -or [int](Read-Database $backup 'verify') -ne $evidence.originalSchema) { throw 'Legacy backup did not preserve the fixture.' }
    Record-Step 'official-legacy-install-and-sqlite-backup'

    $failure = $null
    try {
        $env:OCTETLEDGER_TEST_FAIL_AFTER_REPLACEMENT = '1'
        & "$new\install.ps1"
    }
    catch { $failure = $_.Exception.Message }
    finally { Remove-Item Env:OCTETLEDGER_TEST_FAIL_AFTER_REPLACEMENT -ErrorAction SilentlyContinue }
    if ($failure -notlike '*Simulated post-replacement failure*' -or (Get-FileHash $installed).Hash -ne $oldHash) {
        throw "The failed candidate update did not restore the official executable. Actual error: $failure"
    }
    Assert-Registration $true
    $recoveredWorker = Wait-For { Get-Worker } 'automatic legacy worker recovery'
    if ($recoveredWorker.ProcessId -eq $firstLegacy.worker.ProcessId -or (Get-FileHash $settings).Hash -ne $settingsHash -or
        [int](Read-Database $database 'verify') -ne $evidence.originalSchema) { throw 'Legacy rollback changed data or did not restart the collector.' }
    Record-Step 'failed-update-restored-legacy-worker-registration-settings-and-data'

    & "$new\install.ps1"
    if ((Get-FileHash $installed).Hash -ne $newHash -or
        (Get-FileHash "$installDirectory\octetledger.rollback.exe").Hash -ne $oldHash) { throw 'Incorrect installed candidate or preserved rollback binary.' }
    $evidence.candidateVersion = & $installed version
    Assert-Registration $false
    $first = Wait-For { Get-Content -LiteralPath $ready -Raw -ErrorAction SilentlyContinue } 'candidate first automatic collection'
    $firstWorker = Wait-For { Get-Worker } 'candidate background worker'
    $next = Wait-For {
        $current = Get-Content -LiteralPath $ready -Raw -ErrorAction SilentlyContinue
        if ($current -and [DateTimeOffset]$current -gt [DateTimeOffset]$first) { $current }
    } 'candidate second automatic collection'
    if ((Get-Worker).ProcessId -ne $firstWorker.ProcessId) { throw 'The second collection did not come from the same worker.' }
    $evidence.readyUtc = @($first.Trim(), $next.Trim())
    $evidence.collectionSamplesUtc = @(Read-Database $database 'collections')
    $evidence.migratedSchema = [int](Read-Database $database 'verify')
    if ($evidence.migratedSchema -ne 6 -or (Get-FileHash $settings).Hash -ne $settingsHash) { throw 'Upgrade did not preserve the settings or produce SQLite schema 6.' }
    Record-Step 'candidate-two-automatic-collections-and-migrated-data-preserved'

    & "$installDirectory\rollback.ps1"
    if ((Get-FileHash $installed).Hash -ne $oldHash -or (Get-FileHash $settings).Hash -ne $settingsHash -or
        [int](Read-Database $database 'verify') -ne $evidence.migratedSchema) { throw 'Explicit rollback changed data or failed to restore the published executable.' }
    Assert-Registration $true
    $null = Wait-For { Get-Worker } 'legacy collection after explicit rollback'
    Record-Step 'explicit-rollback-restored-legacy-collector-and-kept-migrated-data'
    $evidence.status = 'pass'
}
catch {
    $evidence.error = $_.Exception.Message
    throw
}
finally {
    try {
        if (Test-Path -LiteralPath $installed) { & $installed collector uninstall | Out-Null }
    }
    finally {
        [Environment]::SetEnvironmentVariable('Path', $originalPath, 'User')
        $evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $evidenceFile
    }
}
