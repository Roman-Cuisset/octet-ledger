[CmdletBinding()]
param(
    [string]$Source,
    [int]$WaitForProcessId = 0,
    [ValidateRange(1, 300)]
    [int]$WaitForProcessTimeoutSeconds = 30,
    [string]$CleanupDirectory,
    [ValidateRange(1, 300)]
    [int]$ExecutableTimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'process.ps1')
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
            Remove-Item -LiteralPath $stagedExecutable -Force -ErrorAction SilentlyContinue
            throw
        }
        Start-Sleep -Milliseconds 250
    }
}
if (-not $staged) {
    throw 'OctetLedger executable could not be staged.'
}

# Validate the staged executable before interrupting a working installation.
try {
    $stagedVersion = Invoke-OctetLedgerExecutable $stagedExecutable @('version') $ExecutableTimeoutSeconds
    if ($stagedVersion.Output -notmatch '^OctetLedger\s+') {
        throw 'The staged OctetLedger executable failed its startup validation. The existing installation was not changed.'
    }
}
catch {
    Remove-Item -LiteralPath $stagedExecutable -Force -ErrorAction SilentlyContinue
    throw
}

$replacementMade = $false
$pathAdded = $false
try {
    if ($hadExistingInstallation) {
        # Preserve the launcher and Run registration throughout the update.
        Invoke-OctetLedgerExecutable $installedExecutable @('collector', 'stop') $ExecutableTimeoutSeconds | Out-Null

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
    # The user explicitly ran this installer and the staged executable passed preflight.
    # Trust only the installed copy: retain the download's Internet mark and all Windows policies.
    Unblock-File -LiteralPath $installedExecutable

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
        $pathAdded = $true
    }
    $rollbackScript = Join-Path $PSScriptRoot 'rollback.ps1'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'process.ps1') -Destination (Join-Path $installDirectory 'process.ps1') -Force
    if (Test-Path -LiteralPath $rollbackScript) {
        Copy-Item -LiteralPath $rollbackScript -Destination (Join-Path $installDirectory 'rollback.ps1') -Force
    }

    Write-Host "OctetLedger installed at: $installedExecutable"
    Invoke-OctetLedgerExecutable $installedExecutable @('collector', 'install') $ExecutableTimeoutSeconds | Out-Null
    if ($hadExistingInstallation -and (Test-Path -LiteralPath $operationBackup)) {
        Move-Item -LiteralPath $operationBackup -Destination $rollbackExecutable -Force
    }
}
catch {
    $failure = $_
    if ($hadExistingInstallation) {
        $restartAction = 'start'
        if ($replacementMade -and (Test-Path -LiteralPath $operationBackup)) {
            try { Invoke-OctetLedgerExecutable $installedExecutable @('collector', 'uninstall') $ExecutableTimeoutSeconds | Out-Null } catch { Write-Warning "Could not unregister the rejected version: $_" }
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
                    [System.IO.File]::Replace($operationBackup, $installedExecutable, $stagedExecutable, $true)
                    break
                }
                catch {
                    if ($attempt -eq 20) { throw }
                    Start-Sleep -Milliseconds 250
                }
            }
            Unblock-File -LiteralPath $installedExecutable
            # The rejected version may have replaced the prior launcher or changed its registration.
            $restartAction = 'install'
        }
        try { Invoke-OctetLedgerExecutable $installedExecutable @('collector', $restartAction) $ExecutableTimeoutSeconds | Out-Null } catch { Write-Warning "Could not restart the preserved version: $_" }
    } elseif ($replacementMade) {
        try { Invoke-OctetLedgerExecutable $installedExecutable @('collector', 'uninstall') $ExecutableTimeoutSeconds | Out-Null } catch { Write-Warning "Could not unregister the failed installation: $_" }
        Remove-Item -LiteralPath $installedExecutable -Force -ErrorAction SilentlyContinue
    }
    if ($pathAdded) {
        $currentPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        $remainingEntries = $currentPath -split ';' | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne $installDirectory
        }
        [Environment]::SetEnvironmentVariable('Path', ($remainingEntries -join ';'), 'User')
    }
    throw $failure
}
finally {
    Remove-Item -LiteralPath $stagedExecutable -Force -ErrorAction SilentlyContinue
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
