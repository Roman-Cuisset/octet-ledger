[CmdletBinding()]
param(
    [string]$Source,
    [int]$WaitForProcessId = 0,
    [ValidateRange(1, 300)]
    [int]$WaitForProcessTimeoutSeconds = 30,
    [string]$CleanupDirectory
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Source)) {
    $packagedExecutable = Join-Path $PSScriptRoot 'octetledger.exe'
    $repositoryExecutable = Join-Path $PSScriptRoot '..\artifacts\win-x64\octetledger.exe'
    $Source = if (Test-Path -LiteralPath $packagedExecutable) {
        $packagedExecutable
    } else {
        $repositoryExecutable
    }
}
$resolvedSource = (Resolve-Path -LiteralPath $Source).Path
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\OctetLedger'
$installedExecutable = Join-Path $installDirectory 'octetledger.exe'
$updateId = [Guid]::NewGuid().ToString('N')
$stagedExecutable = Join-Path $installDirectory "octetledger.update.$updateId.exe"
$operationBackup = Join-Path $installDirectory "octetledger.previous.$updateId.exe"
$rollbackExecutable = Join-Path $installDirectory 'octetledger.rollback.exe'

if ($WaitForProcessId -gt 0) {
    $parentProcess = Get-Process -Id $WaitForProcessId -ErrorAction SilentlyContinue
    if ($null -ne $parentProcess) {
        try {
            Wait-Process -InputObject $parentProcess -Timeout $WaitForProcessTimeoutSeconds -ErrorAction Stop
        }
        catch {
            $stillRunning = Get-Process -Id $WaitForProcessId -ErrorAction SilentlyContinue
            if ($null -ne $stillRunning) {
                throw "The calling OctetLedger process did not exit within $WaitForProcessTimeoutSeconds seconds. The update was not installed."
            }
        }
        if ($null -ne (Get-Process -Id $WaitForProcessId -ErrorAction SilentlyContinue)) {
            throw "The calling OctetLedger process is still running. The update was not installed."
        }
    }
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
$hadExistingInstallation = Test-Path -LiteralPath $installedExecutable

# Stage the new binary before stopping the working collector.
$staged = $false
for ($attempt = 1; $attempt -le 20; $attempt++) {
    try {
        Copy-Item -LiteralPath $resolvedSource -Destination $stagedExecutable -Force
        $staged = $true
        break
    }
    catch {
        if ($attempt -eq 20) {
            throw
        }
        Start-Sleep -Milliseconds 250
    }
}
if (-not $staged) {
    throw 'OctetLedger executable could not be staged.'
}

# Validate the staged executable before interrupting a working installation.
$stagedVersion = & $stagedExecutable version 2>&1
if ($LASTEXITCODE -ne 0 -or ($stagedVersion -join "`n") -notmatch '^OctetLedger\s+') {
    Remove-Item -LiteralPath $stagedExecutable -Force -ErrorAction SilentlyContinue
    throw 'The staged OctetLedger executable failed its startup validation. The existing installation was not changed.'
}

$replacementMade = $false
try {
    if ($hadExistingInstallation) {
        # Preserve the launcher and Run registration throughout the update.
        & $installedExecutable collector stop 2>$null | Out-Null

        # Clean up a collector left behind by an interrupted older update.
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
                [System.IO.File]::Replace($stagedExecutable, $installedExecutable, $operationBackup, $true)
                break
            }
            catch {
                if ($attempt -eq 20) { throw }
                Start-Sleep -Milliseconds 250
            }
        }
    } else {
        Move-Item -LiteralPath $stagedExecutable -Destination $installedExecutable
    }
    $replacementMade = $true

    if ($env:OCTETLEDGER_TEST_FAIL_AFTER_REPLACEMENT -eq '1') {
        throw 'Simulated post-replacement failure for installer rollback testing.'
    }

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($null -eq $userPath) {
        $userPath = ''
    }
    $pathEntries = $userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    if ($pathEntries -notcontains $installDirectory) {
        $newUserPath = (($pathEntries + $installDirectory) -join ';')
        [Environment]::SetEnvironmentVariable('Path', $newUserPath, 'User')
    }
    $rollbackScript = Join-Path $PSScriptRoot 'rollback.ps1'
    if (Test-Path -LiteralPath $rollbackScript) {
        Copy-Item -LiteralPath $rollbackScript -Destination (Join-Path $installDirectory 'rollback.ps1') -Force
    }

    Write-Host "OctetLedger installed at: $installedExecutable"
    & $installedExecutable collector install
    if ($LASTEXITCODE -ne 0) {
        throw "OctetLedger was copied, but its automatic collector could not be installed."
    }
    if ($hadExistingInstallation -and (Test-Path -LiteralPath $operationBackup)) {
        Move-Item -LiteralPath $operationBackup -Destination $rollbackExecutable -Force
    }
}
catch {
    if ($hadExistingInstallation) {
        if ($replacementMade -and (Test-Path -LiteralPath $operationBackup)) {
            try { & $installedExecutable collector stop 2>$null | Out-Null } catch {}
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
                    [System.IO.File]::Replace($operationBackup, $installedExecutable, $null, $true)
                    break
                }
                catch {
                    if ($attempt -eq 20) { throw }
                    Start-Sleep -Milliseconds 250
                }
            }
        }
        try { & $installedExecutable collector start 2>$null | Out-Null } catch {}
    }
    throw
}
finally {
    Remove-Item -LiteralPath $stagedExecutable -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $operationBackup -Force -ErrorAction SilentlyContinue
}
Write-Host "Open a new terminal, then run: octetledger"
if (-not [string]::IsNullOrWhiteSpace($CleanupDirectory)) {
    $resolvedCleanup = [System.IO.Path]::GetFullPath($CleanupDirectory)
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedCleanup.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedCleanup).StartsWith('octetledger-update-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedCleanup -Recurse -Force -ErrorAction SilentlyContinue
    }
}
