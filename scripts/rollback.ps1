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
        break
    }
    catch {
        if ($attempt -eq 20) { throw }
        Start-Sleep -Milliseconds 250
    }
}
$version = & $installedExecutable version 2>&1
if ($LASTEXITCODE -ne 0 -or ($version -join "`n") -notmatch '^OctetLedger\s+') {
    [System.IO.File]::Replace($failedExecutable, $installedExecutable, $null, $true)
    throw 'The previous executable failed validation; the current version was restored.'
}
Remove-Item -LiteralPath $failedExecutable -Force -ErrorAction SilentlyContinue
& $installedExecutable collector install
if ($LASTEXITCODE -ne 0) { throw 'Rollback completed, but the collector could not be restarted.' }
Write-Host "Rollback complete: $($version -join ' ')"
