[CmdletBinding()]
param(
    [string]$Source
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
$backupExecutable = Join-Path $installDirectory "octetledger.previous.$updateId.exe"

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

        [System.IO.File]::Replace($stagedExecutable, $installedExecutable, $backupExecutable, $true)
    } else {
        Move-Item -LiteralPath $stagedExecutable -Destination $installedExecutable
    }
    $replacementMade = $true

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($null -eq $userPath) {
        $userPath = ''
    }
    $pathEntries = $userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    if ($pathEntries -notcontains $installDirectory) {
        $newUserPath = (($pathEntries + $installDirectory) -join ';')
        [Environment]::SetEnvironmentVariable('Path', $newUserPath, 'User')
    }

    Write-Host "OctetLedger installed at: $installedExecutable"
    & $installedExecutable collector install
    if ($LASTEXITCODE -ne 0) {
        throw "OctetLedger was copied, but its automatic collector could not be installed."
    }
}
catch {
    if ($hadExistingInstallation) {
        if ($replacementMade -and (Test-Path -LiteralPath $backupExecutable)) {
            try { & $installedExecutable collector stop 2>$null | Out-Null } catch {}
            [System.IO.File]::Replace($backupExecutable, $installedExecutable, $null, $true)
        }
        try { & $installedExecutable collector start 2>$null | Out-Null } catch {}
    }
    throw
}
finally {
    Remove-Item -LiteralPath $stagedExecutable -Force -ErrorAction SilentlyContinue
}
Remove-Item -LiteralPath $backupExecutable -Force -ErrorAction SilentlyContinue
Write-Host "Open a new terminal, then run: octetledger"
