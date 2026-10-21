<#
.SYNOPSIS
Runs the complete isolated native Windows installation/recovery/release gate.
.DESCRIPTION
Requires PowerShell 7 and .NET 10 SDK (to compile a real incompatible rollback fixture).
Never targets the user's installed collector. -EvidencePath explicitly retains only
fixture version/boot/session/timestamps and step results; temporary installations,
fixture processes, installation-scoped tasks/startup registry and PATH changes are cleaned.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [string]$EvidencePath
)

$ErrorActionPreference = 'Stop'
$evidence = [ordered]@{
    scenario = 'install-update-stop-crash-launcher-death-stall-rollback-uninstall'
    status = 'running'
    startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    bootUtc = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('O')
    sessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    steps = [System.Collections.Generic.List[object]]::new()
}
function Record-Step([string]$Name) {
    $evidence.steps.Add(@{ name = $Name; status = 'pass'; completedUtc = [DateTimeOffset]::UtcNow.ToString('O') })
}
function Get-Sha256Hex([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
    finally { $sha.Dispose(); $stream.Dispose() }
}
function Copy-ItemWithRetry([string]$Source, [string]$Destination) {
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try {
            Copy-Item -LiteralPath $Source -Destination $Destination -Force
            return
        }
        catch {
            if ($attempt -eq 20) { throw }
            Start-Sleep -Milliseconds 250
        }
    }
}
$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("octetledger-lifecycle-" + [Guid]::NewGuid().ToString('N'))
$packageDirectory = Join-Path $testRoot 'package'
$testLocalAppData = Join-Path $testRoot 'localappdata'
$originalLocalAppData = $env:LOCALAPPDATA
$originalUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$originalFailureInjection = $env:OCTETLEDGER_TEST_FAIL_AFTER_REPLACEMENT
Remove-Item Env:OCTETLEDGER_TEST_FAIL_AFTER_REPLACEMENT -ErrorAction SilentlyContinue

try {
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $testLocalAppData -Force | Out-Null
    Expand-Archive -LiteralPath $resolvedPackage -DestinationPath $packageDirectory
    $env:LOCALAPPDATA = $testLocalAppData
    $installScript = Join-Path $packageDirectory 'install.ps1'
    $uninstallScript = Join-Path $packageDirectory 'uninstall.ps1'
    $installedExecutable = Join-Path $testLocalAppData 'Programs\OctetLedger\octetledger.exe'
    $testDataDirectory = Join-Path $testLocalAppData 'OctetLedger'
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $identityBytes = [System.Text.Encoding]::UTF8.GetBytes([System.IO.Path]::GetFullPath($testDataDirectory).ToUpperInvariant())
        $installationId = ([System.BitConverter]::ToString($sha.ComputeHash($identityBytes))).Replace('-', '').Substring(0, 16)
    }
    finally { $sha.Dispose() }
    $watchdogTaskName = "OctetLedger Watchdog $installationId"
    $startupValueName = "OctetLedger Collector $installationId"

    & $installScript
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $installedExecutable)) {
        throw 'Fresh installation failed.'
    }
    $evidence.version = (& $installedExecutable version) -join ' '
    if ($LASTEXITCODE -ne 0) { throw 'Installed version could not be read.' }
    & $installedExecutable database check
    if ($LASTEXITCODE -ne 0) { throw 'Database integrity check failed after installation.' }

    $readyPath = Join-Path $testDataDirectory 'collector.ready'
    for ($attempt = 0; $attempt -lt 60 -and -not (Test-Path -LiteralPath $readyPath); $attempt++) {
        Start-Sleep -Milliseconds 500
    }
    if (-not (Test-Path -LiteralPath $readyPath)) { throw 'The initial collector did not record a successful collection.' }
    Record-Step 'fresh-install-and-first-successful-collection'
    $readyBeforeStop = Get-Content -LiteralPath $readyPath -Raw
    & $installedExecutable collector stop
    $stopped = & $installedExecutable collector status 2>&1
    if (($stopped -join "`n") -notmatch 'Installed, stopped') { throw 'Collector did not report a stopped state.' }
    & $installedExecutable collector ensure
    if ($LASTEXITCODE -ne 0) { throw 'A deliberately stopped collector failed its watchdog check.' }
    & schtasks.exe /Run /TN $watchdogTaskName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The independent watchdog task could not be run.' }
    Start-Sleep -Seconds 2
    $stopped = & $installedExecutable collector status 2>&1
    if (($stopped -join "`n") -notmatch 'Installed, stopped') {
        throw 'The watchdog restarted a deliberately stopped collector.'
    }
    if (Test-Path -LiteralPath (Join-Path $testDataDirectory 'collector.pid')) {
        throw 'A deliberately stopped collector retained a worker PID.'
    }
    if (-not (Test-Path -LiteralPath $readyPath) -or
        (Get-Content -LiteralPath $readyPath -Raw) -ne $readyBeforeStop) {
        throw 'Stopping or checking the collector changed its last successful collection timestamp.'
    }
    Record-Step 'deliberate-stop-survives-independent-watchdog'
    & $installedExecutable collector start
    if ($LASTEXITCODE -ne 0) { throw 'Collector did not restart.' }

    # Give the update a distinct hash while retaining a working Windows executable.
    $knownGoodHash = Get-Sha256Hex $installedExecutable
    $updatedExecutable = Join-Path $testRoot 'updated.exe'
    Copy-Item -LiteralPath (Join-Path $packageDirectory 'octetledger.exe') -Destination $updatedExecutable
    Add-Content -LiteralPath $updatedExecutable -Value 'OctetLedger lifecycle update overlay' -Encoding Ascii
    & $updatedExecutable version
    if ($LASTEXITCODE -ne 0) { throw 'The distinct update fixture did not remain executable.' }
    & $installScript -Source $updatedExecutable
    if ($LASTEXITCODE -ne 0) { throw 'In-place update failed.' }
    $updatedHash = Get-Sha256Hex $installedExecutable
    if ($updatedHash -eq $knownGoodHash) { throw 'Update fixture did not change the installed executable.' }
    $installedRollback = Join-Path $testLocalAppData 'Programs\OctetLedger\rollback.ps1'
    $rollbackExecutable = Join-Path $testLocalAppData 'Programs\OctetLedger\octetledger.rollback.exe'
    if (-not (Test-Path -LiteralPath $installedRollback) -or -not (Test-Path -LiteralPath $rollbackExecutable)) {
        throw 'In-place update did not retain rollback files.'
    }
    Record-Step 'in-place-update-retains-rollback'
    # Reject a broken rollback candidate before stopping or replacing the working version.
    $savedRollback = Join-Path $testRoot 'saved-rollback.exe'
    Copy-Item -LiteralPath $rollbackExecutable -Destination $savedRollback
    Set-Content -LiteralPath $rollbackExecutable -Value 'invalid executable' -Encoding Ascii
    $rollbackRejected = $false
    try { & $installedRollback } catch { $rollbackRejected = $true }
    if (-not $rollbackRejected -or (Get-Sha256Hex $installedExecutable) -ne $updatedHash) {
        throw 'Invalid rollback candidate changed the working executable.'
    }
    $status = & $installedExecutable collector status 2>&1
    if (($status -join "`n") -notmatch 'Running|first collection pending') {
        throw 'Invalid rollback candidate stopped the working collector.'
    }
    # Version-only validation is insufficient: this real candidate deliberately rejects the current database.
    $fixtureRuntime = switch ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) {
        'X64' { 'win-x64' }
        'Arm64' { 'win-arm64' }
        default { throw 'Lifecycle rollback fixture requires native Windows x64 or ARM64.' }
    }
    $candidateDirectory = Join-Path $testRoot 'incompatible-rollback'
    & dotnet publish (Join-Path $PSScriptRoot 'validation-rollback/OctetLedger.RollbackCandidate.csproj') `
        --configuration Release --runtime $fixtureRuntime --self-contained true `
        -p:PublishSingleFile=true --artifacts-path (Join-Path $testRoot 'build') --output $candidateDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Unable to build the real incompatible rollback fixture; .NET 10 SDK is required.' }
    Copy-ItemWithRetry (Join-Path $candidateDirectory 'OctetLedger.RollbackCandidate.exe') $rollbackExecutable
    & $rollbackExecutable version
    if ($LASTEXITCODE -ne 0) { throw 'Schema-incompatible rollback fixture did not pass executable version validation.' }
    $workerBeforeRejectedRollback = Get-Content -LiteralPath (Join-Path $testDataDirectory 'collector.pid') -Raw
    $readyBeforeRejectedRollback = [DateTimeOffset]::Parse((Get-Content -LiteralPath $readyPath -Raw).Trim())
    $schemaRejected = $false
    try { & $installedRollback } catch { $schemaRejected = $true }
    $schemaProbeMarker = Join-Path (Split-Path -Parent $installedExecutable) 'rollback-validation-database-check'
    if (-not $schemaRejected -or -not (Test-Path -LiteralPath $schemaProbeMarker) -or
        (Get-Sha256Hex $installedExecutable) -ne $updatedHash) {
        throw 'Rollback did not reject a version-valid candidate after actually checking database compatibility.'
    }
    if ((Get-Content -LiteralPath (Join-Path $testDataDirectory 'collector.pid') -Raw) -ne $workerBeforeRejectedRollback -or
        (Test-Path -LiteralPath (Join-Path $testDataDirectory 'collector.stop')) -or
        -not (Test-Path -LiteralPath $readyPath) -or
        [DateTimeOffset]::Parse((Get-Content -LiteralPath $readyPath -Raw).Trim()) -lt $readyBeforeRejectedRollback) {
        throw 'Schema-incompatible rollback interrupted the working collector or lost its last successful collection.'
    }
    Remove-Item -LiteralPath $schemaProbeMarker -Force
    Record-Step 'version-valid-database-incompatible-rollback-rejected-without-interruption'
    Copy-ItemWithRetry $savedRollback $rollbackExecutable
    & $installedRollback
    if ($LASTEXITCODE -ne 0 -or (Get-Sha256Hex $installedExecutable) -ne $knownGoodHash) {
        throw 'Explicit rollback did not restore the previous executable.'
    }
    Record-Step 'reject-invalid-rollback-and-restore-previous-executable'

    # A live parent beyond the timeout must abort before collector stop or executable replacement.
    $parent = Start-Process powershell.exe -ArgumentList '-NoProfile', '-Command', 'Start-Sleep -Seconds 30' -PassThru
    $parentTimeoutRejected = $false
    try {
        try {
            & $installScript -WaitForProcessId $parent.Id -WaitForProcessTimeoutSeconds 1
        }
        catch {
            $parentTimeoutRejected = $_.Exception.Message -match 'did not exit within'
        }
    }
    finally {
        Stop-Process -Id $parent.Id -Force -ErrorAction SilentlyContinue
    }
    if (-not $parentTimeoutRejected) { throw 'Installer did not fail closed when its parent remained alive.' }
    if ((Get-Sha256Hex $installedExecutable) -ne $knownGoodHash) {
        throw 'Working executable changed after a parent-process timeout.'
    }

    # A bad staged executable must be rejected before the working binary is replaced.
    $invalidExecutable = Join-Path $testRoot 'invalid.exe'
    Set-Content -LiteralPath $invalidExecutable -Value 'invalid executable' -Encoding Ascii
    $rejected = $false
    try { & $installScript -Source $invalidExecutable } catch { $rejected = $true }
    if (-not $rejected) { throw 'Installer accepted an invalid staged executable.' }
    if ((Get-Sha256Hex $installedExecutable) -ne $knownGoodHash) {
        throw 'Working executable changed after a rejected update.'
    }
    Record-Step 'reject-live-parent-and-invalid-update-without-replacement'

    # Force a failure after replacement: the installer must restore its executable backup.
    $rolledBack = $false
    try {
        $env:OCTETLEDGER_TEST_FAIL_AFTER_REPLACEMENT = '1'
        try { & $installScript -Source $updatedExecutable } catch { $rolledBack = $true; $replacementFailure = $_ }
    }
    finally {
        Remove-Item Env:OCTETLEDGER_TEST_FAIL_AFTER_REPLACEMENT -ErrorAction SilentlyContinue
    }
    if (-not $rolledBack) { throw 'Forced post-replacement update failure did not occur.' }
    if ((Get-Sha256Hex $installedExecutable) -ne $knownGoodHash) {
        throw "Installer did not restore the working executable after a post-replacement failure. Original error: $replacementFailure"
    }
    & $installedExecutable collector start
    if ($LASTEXITCODE -ne 0) { throw 'Collector did not restart after update rollback.' }
    Record-Step 'post-replacement-failure-restores-working-version'

    # Simulate an abrupt collector crash and require the launcher to restart it.
    $collector = Get-CimInstance Win32_Process -Filter "Name='octetledger.exe'" |
        Where-Object { $_.ExecutablePath -eq $installedExecutable -and $_.CommandLine -match '--background' } |
        Select-Object -First 1
    if ($null -eq $collector) { throw 'Background collector process was not found.' }
    Stop-Process -Id $collector.ProcessId -Force
    $restarted = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 500
        $status = & $installedExecutable collector status 2>&1
        if (($status -join "`n") -match 'Running|first collection pending') { $restarted = $true; break }
    }
    if (-not $restarted) { throw 'Launcher did not restart the collector after an abrupt stop.' }
    Record-Step 'worker-crash-recovery'

    # Kill both the worker and its local launcher: the independent scheduled watchdog must recover them.
    $launchers = @(Get-CimInstance Win32_Process -Filter "Name='wscript.exe'" |
        Where-Object { $_.CommandLine -like "*$(Join-Path $testDataDirectory 'collector.vbs')*" })
    $workers = @(Get-CimInstance Win32_Process -Filter "Name='octetledger.exe'" |
        Where-Object { $_.ExecutablePath -eq $installedExecutable -and $_.CommandLine -match '--background' })
    if ($launchers.Count -eq 0 -or $workers.Count -eq 0) { throw 'The isolated collector and launcher were not both running.' }
    foreach ($launcher in $launchers) { Stop-Process -Id $launcher.ProcessId -Force -ErrorAction SilentlyContinue }
    foreach ($worker in $workers) { Stop-Process -Id $worker.ProcessId -Force -ErrorAction SilentlyContinue }
    $recovered = $false
    $recoveryTimer = [System.Diagnostics.Stopwatch]::StartNew()
    while ($recoveryTimer.Elapsed.TotalSeconds -lt 95) {
        Start-Sleep -Milliseconds 500
        $status = & $installedExecutable collector status 2>&1
        if (($status -join "`n") -match 'Collector: Running') { $recovered = $true; break }
    }
    if (-not $recovered) {
        $taskDetails = (& schtasks.exe /Query /TN $watchdogTaskName /V /FO LIST 2>&1) -join "`n"
        $collectorLog = (Get-Content -LiteralPath (Join-Path $testDataDirectory 'collector.log') -ErrorAction SilentlyContinue) -join "`n"
        $deliberatelyStopped = Test-Path -LiteralPath (Join-Path $testDataDirectory 'collector.stop')
        throw "The independent watchdog did not recover the worker after its launcher was killed. Stop marker: $deliberatelyStopped`n$taskDetails`n$collectorLog"
    }
    Record-Step 'worker-and-launcher-death-independent-watchdog-recovery'

    # A live but stalled worker must be replaced after successful collection becomes stale.
    if (-not ('OctetLedgerLifecycleNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class OctetLedgerLifecycleNative {
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")]
    public static extern int NtSuspendProcess(IntPtr handle);
}
'@
    }
    $stalledWorker = Get-CimInstance Win32_Process -Filter "Name='octetledger.exe'" |
        Where-Object { $_.ExecutablePath -eq $installedExecutable -and $_.CommandLine -match '--background' } |
        Select-Object -First 1
    if ($null -eq $stalledWorker) { throw 'No recovered worker was available for the stall scenario.' }
    $handle = [OctetLedgerLifecycleNative]::OpenProcess(0x0800, $false, $stalledWorker.ProcessId)
    if ($handle -eq [IntPtr]::Zero) { throw 'The isolated worker could not be opened for suspension.' }
    try {
        if ([OctetLedgerLifecycleNative]::NtSuspendProcess($handle) -ne 0) {
            throw 'The isolated worker could not be suspended.'
        }
    }
    finally { [OctetLedgerLifecycleNative]::CloseHandle($handle) | Out-Null }
    $lastReady = [DateTimeOffset]::Parse((Get-Content -LiteralPath $readyPath -Raw).Trim())
    $recoveredFromStall = $false
    $stallTimer = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stallTimer.Elapsed.TotalSeconds -lt 285) {
        Start-Sleep -Milliseconds 500
        $identity = (Get-Content -LiteralPath (Join-Path $testDataDirectory 'collector.pid') -Raw -ErrorAction SilentlyContinue) -split '\|'
        if ($identity.Count -gt 1 -and [int]$identity[0] -ne $stalledWorker.ProcessId -and
            (Test-Path -LiteralPath $readyPath) -and
            [DateTimeOffset]::Parse((Get-Content -LiteralPath $readyPath -Raw).Trim()) -gt $lastReady) {
            $recoveredFromStall = $true
            break
        }
    }
    if (-not $recoveredFromStall) { throw 'The watchdog did not replace the stalled worker and resume successful collection.' }
    Write-Host 'Verified recovery after worker crash, worker-plus-launcher crash, and a suspended worker.'
    Record-Step 'stalled-live-worker-replaced-and-collection-resumed'

    & $uninstallScript
    if ($LASTEXITCODE -ne 0) { throw 'Uninstall failed.' }
    if (Test-Path -LiteralPath $installedExecutable) { throw 'Uninstall retained the installed executable.' }
    $uninstallTimer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $remainingWorkers = @(Get-CimInstance Win32_Process -Filter "Name='octetledger.exe'" |
            Where-Object { $_.ExecutablePath -eq $installedExecutable })
        $remainingLaunchers = @(Get-CimInstance Win32_Process -Filter "Name='wscript.exe'" |
            Where-Object { $_.CommandLine -like "*$(Join-Path $testDataDirectory 'collector.vbs')*" })
        if ($remainingWorkers.Count -eq 0 -and $remainingLaunchers.Count -eq 0) { break }
        Start-Sleep -Milliseconds 200
    } while ($uninstallTimer.Elapsed.TotalSeconds -lt 10)
    if ($remainingWorkers.Count -ne 0 -or $remainingLaunchers.Count -ne 0) {
        throw 'Uninstall retained an isolated collector or launcher process.'
    }
    & schtasks.exe /Query /TN $watchdogTaskName 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'Uninstall retained its independent watchdog task.' }
    $startup = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
    if ($null -ne $startup -and $null -ne $startup.PSObject.Properties[$startupValueName]) {
        throw 'Uninstall retained its startup registry value.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $testDataDirectory 'octetledger.db'))) {
        throw 'Uninstall did not preserve collected statistics.'
    }
    Record-Step 'uninstall-removes-executable-processes-task-and-startup-preserves-data'

    # A failed fresh installation must not leave its new executable or PATH entry behind.
    $freshFailure = $false
    try {
        $env:OCTETLEDGER_TEST_FAIL_AFTER_REPLACEMENT = '1'
        try { & $installScript } catch { $freshFailure = $true }
    }
    finally { Remove-Item Env:OCTETLEDGER_TEST_FAIL_AFTER_REPLACEMENT -ErrorAction SilentlyContinue }
    if (-not $freshFailure -or (Test-Path -LiteralPath $installedExecutable)) {
        throw 'A failed fresh installation retained the newly installed executable.'
    }
    $installDirectory = Split-Path -Parent $installedExecutable
    if (([Environment]::GetEnvironmentVariable('Path', 'User') -split ';') -contains $installDirectory) {
        throw 'A failed fresh installation retained its PATH entry.'
    }
    Record-Step 'failed-fresh-install-removes-executable-and-path'
    $evidence.status = 'pass'
}
catch {
    $evidence.status = 'fail'
    $evidence.failure = $_.Exception.Message
    throw
}
finally {
    $env:LOCALAPPDATA = $testLocalAppData
    if (Test-Path -LiteralPath $testLocalAppData) {
        $testExecutable = Join-Path $testLocalAppData 'Programs\OctetLedger\octetledger.exe'
        if (Test-Path -LiteralPath $testExecutable) {
            try { & $testExecutable collector uninstall 2>$null | Out-Null } catch {}
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($watchdogTaskName)) {
        try { & schtasks.exe /Delete /TN $watchdogTaskName /F 2>$null | Out-Null } catch {}
    }
    if (-not [string]::IsNullOrWhiteSpace($startupValueName)) {
        Remove-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $startupValueName -ErrorAction SilentlyContinue
    }
    # Kill only fixture-owned processes, including a suspended worker after a failed assertion.
    Get-CimInstance Win32_Process -Filter "Name='octetledger.exe' OR Name='wscript.exe'" |
        Where-Object {
            ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($testRoot + '\', [StringComparison]::OrdinalIgnoreCase)) -or
            ($_.Name -eq 'wscript.exe' -and $_.CommandLine -like "*$testRoot*")
        } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    $env:LOCALAPPDATA = $originalLocalAppData
    $env:OCTETLEDGER_TEST_FAIL_AFTER_REPLACEMENT = $originalFailureInjection
    [Environment]::SetEnvironmentVariable('Path', $originalUserPath, 'User')
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
    $evidence.completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
        $resolvedEvidence = [System.IO.Path]::GetFullPath($EvidencePath)
        New-Item -ItemType Directory -Path (Split-Path -Parent $resolvedEvidence) -Force | Out-Null
        $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resolvedEvidence -Encoding utf8
    }
}
