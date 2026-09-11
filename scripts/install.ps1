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

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
if (Test-Path -LiteralPath $installedExecutable) {
    & $installedExecutable collector uninstall 2>$null | Out-Null

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
}
$copied = $false
for ($attempt = 1; $attempt -le 20; $attempt++) {
    try {
        Copy-Item -LiteralPath $resolvedSource -Destination $installedExecutable -Force
        $copied = $true
        break
    }
    catch {
        if ($attempt -eq 20) {
            throw
        }
        Start-Sleep -Milliseconds 250
    }
}
if (-not $copied) {
    throw 'OctetLedger executable could not be updated.'
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

Write-Host "OctetLedger installed at: $installedExecutable"
& $installedExecutable collector install
if ($LASTEXITCODE -ne 0) {
    throw "OctetLedger was copied, but its automatic collector could not be installed."
}
Write-Host "Open a new terminal, then run: octetledger"
