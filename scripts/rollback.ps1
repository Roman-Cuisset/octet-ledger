[CmdletBinding()]
param(
    [int]$WaitForProcessId = 0,
    [ValidateRange(1, 300)]
    [int]$WaitForProcessTimeoutSeconds = 30,
    [ValidateRange(1, 300)]
    [int]$ExecutableTimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'process.ps1')
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\OctetLedger'
$installedExecutable = Join-Path $installDirectory 'octetledger.exe'
$previousExecutable = Join-Path $installDirectory 'octetledger.rollback.exe'
$failedExecutable = Join-Path $installDirectory ("octetledger.failed." + [Guid]::NewGuid().ToString('N') + '.exe')

if (-not (Test-Path -LiteralPath $previousExecutable)) {
    throw 'No previous OctetLedger version is available for rollback.'
}
if ($WaitForProcessId -gt 0) {
    $parent = Get-Process -Id $WaitForProcessId -ErrorAction SilentlyContinue
    if ($null -ne $parent) {
        Wait-Process -InputObject $parent -Timeout $WaitForProcessTimeoutSeconds -ErrorAction Stop
    }
}
$previousVersion = Invoke-OctetLedgerExecutable $previousExecutable @('version') $ExecutableTimeoutSeconds
if ($previousVersion.Output -notmatch '^OctetLedger\s+') {
    throw 'The previous executable failed validation. The current installation was not changed.'
}
$databasePath = Join-Path $env:LOCALAPPDATA 'OctetLedger\octetledger.db'
if (Test-Path -LiteralPath $databasePath) {
    try {
        Invoke-OctetLedgerExecutable $previousExecutable @('database', 'check', $databasePath) $ExecutableTimeoutSeconds | Out-Null
    }
    catch [TimeoutException] { throw }
    catch {
        throw [InvalidOperationException]::new(
            "The previous executable cannot read the current database. The current installation was not changed. Restore a matching database backup explicitly before downgrading. $($_.Exception.Message)",
            $_.Exception)
    }
}
$replacementMade = $false
try {
    Invoke-OctetLedgerExecutable $installedExecutable @('collector', 'stop') $ExecutableTimeoutSeconds | Out-Null

    # Clean up any collector process holding the executable.
    Get-CimInstance Win32_Process -Filter "Name='octetledger.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ExecutablePath -eq $installedExecutable -and
            $_.CommandLine -match '\smonitor\s' -and
            $_.CommandLine -match '--background'
        } |
        ForEach-Object {
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }

    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try {
            [System.IO.File]::Replace($previousExecutable, $installedExecutable, $failedExecutable, $true)
            $replacementMade = $true
            break
        }
        catch {
            if ($attempt -eq 20) { throw }
            Start-Sleep -Milliseconds 250
        }
    }
    # The selected previous binary passed version and database compatibility validation.
    Unblock-File -LiteralPath $installedExecutable
    $version = Invoke-OctetLedgerExecutable $installedExecutable @('version') $ExecutableTimeoutSeconds
    if ($version.Output -notmatch '^OctetLedger\s+') {
        throw 'The previous executable failed validation after replacement.'
    }
    Invoke-OctetLedgerExecutable $installedExecutable @('collector', 'install') $ExecutableTimeoutSeconds | Out-Null
    Remove-Item -LiteralPath $failedExecutable -Force -ErrorAction SilentlyContinue
    Write-Host "Rollback complete: $($version.Output.Trim())"
}
catch {
    $failure = $_
    if ($replacementMade -and (Test-Path -LiteralPath $failedExecutable)) {
        try { Invoke-OctetLedgerExecutable $installedExecutable @('collector', 'stop') $ExecutableTimeoutSeconds | Out-Null } catch { Write-Warning "Could not stop the failed rollback: $_" }
        try {
            [System.IO.File]::Replace($failedExecutable, $installedExecutable, $previousExecutable, $true)
            Unblock-File -LiteralPath $installedExecutable
        }
        catch {
            Write-Warning "Could not restore the current version. Its executable backup is retained at: $failedExecutable"
            throw
        }
    }
    try {
        Invoke-OctetLedgerExecutable $installedExecutable @('collector', 'install') $ExecutableTimeoutSeconds | Out-Null
    }
    catch { Write-Warning "The current version was preserved, but its collector could not be restarted: $_" }
    throw $failure
}
