[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'
$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("octetledger-lifecycle-" + [Guid]::NewGuid().ToString('N'))
$packageDirectory = Join-Path $testRoot 'package'
$testLocalAppData = Join-Path $testRoot 'localappdata'
$originalLocalAppData = $env:LOCALAPPDATA
$originalUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')

try {
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $testLocalAppData -Force | Out-Null
    Expand-Archive -LiteralPath $resolvedPackage -DestinationPath $packageDirectory
    $env:LOCALAPPDATA = $testLocalAppData
    $installScript = Join-Path $packageDirectory 'install.ps1'
    $uninstallScript = Join-Path $packageDirectory 'uninstall.ps1'
    $installedExecutable = Join-Path $testLocalAppData 'Programs\OctetLedger\octetledger.exe'

    & $installScript
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $installedExecutable)) {
        throw 'Fresh installation failed.'
    }
    & $installedExecutable database check
    if ($LASTEXITCODE -ne 0) { throw 'Database integrity check failed after installation.' }

    & $installedExecutable collector stop
    $stopped = & $installedExecutable collector status 2>&1
    if (($stopped -join "`n") -notmatch 'Installed, stopped') { throw 'Collector did not report a stopped state.' }
    & $installedExecutable collector start
    if ($LASTEXITCODE -ne 0) { throw 'Collector did not restart.' }

    # Exercise an in-place update with the same known-good package.
    & $installScript
    if ($LASTEXITCODE -ne 0) { throw 'In-place update failed.' }
    $knownGoodHash = (Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash

    # A bad staged executable must be rejected before the working binary is replaced.
    $invalidExecutable = Join-Path $testRoot 'invalid.exe'
    Set-Content -LiteralPath $invalidExecutable -Value 'invalid executable' -Encoding Ascii
    $rejected = $false
    try { & $installScript -Source $invalidExecutable } catch { $rejected = $true }
    if (-not $rejected) { throw 'Installer accepted an invalid staged executable.' }
    if ((Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash -ne $knownGoodHash) {
        throw 'Working executable changed after a rejected update.'
    }

    # Force a failure after replacement: the installer must restore its executable backup.
    $launcherBlocker = Join-Path $testLocalAppData 'OctetLedger\collector.vbs.tmp'
    New-Item -ItemType Directory -Path $launcherBlocker -Force | Out-Null
    $rolledBack = $false
    try {
        try { & $installScript } catch { $rolledBack = $true }
    }
    finally {
        Remove-Item -LiteralPath $launcherBlocker -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not $rolledBack) { throw 'Forced post-replacement update failure did not occur.' }
    if ((Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash -ne $knownGoodHash) {
        throw 'Installer did not restore the working executable after a post-replacement failure.'
    }
    & $installedExecutable collector start
    if ($LASTEXITCODE -ne 0) { throw 'Collector did not restart after update rollback.' }

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

    & $uninstallScript
}
finally {
    $env:LOCALAPPDATA = $testLocalAppData
    if (Test-Path -LiteralPath $testLocalAppData) {
        $testExecutable = Join-Path $testLocalAppData 'Programs\OctetLedger\octetledger.exe'
        if (Test-Path -LiteralPath $testExecutable) {
            try { & $testExecutable collector uninstall 2>$null | Out-Null } catch {}
        }
    }
    $env:LOCALAPPDATA = $originalLocalAppData
    [Environment]::SetEnvironmentVariable('Path', $originalUserPath, 'User')
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
