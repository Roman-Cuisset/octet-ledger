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
}
Copy-Item -LiteralPath $resolvedSource -Destination $installedExecutable -Force

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
