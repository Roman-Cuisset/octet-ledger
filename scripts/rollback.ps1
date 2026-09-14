[CmdletBinding()]
param(
    [int]$WaitForProcessId = 0,
    [ValidateRange(1, 300)]
    [int]$WaitForProcessTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
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
& $installedExecutable collector stop 2>$null | Out-Null
[System.IO.File]::Replace($previousExecutable, $installedExecutable, $failedExecutable, $true)
$version = & $installedExecutable version 2>&1
if ($LASTEXITCODE -ne 0 -or ($version -join "`n") -notmatch '^OctetLedger\s+') {
    [System.IO.File]::Replace($failedExecutable, $installedExecutable, $null, $true)
    throw 'The previous executable failed validation; the current version was restored.'
}
Remove-Item -LiteralPath $failedExecutable -Force -ErrorAction SilentlyContinue
& $installedExecutable collector install
if ($LASTEXITCODE -ne 0) { throw 'Rollback completed, but the collector could not be restarted.' }
Write-Host "Rollback complete: $($version -join ' ')"
