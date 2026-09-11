[CmdletBinding()]
param(
    [string]$Source
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Source)) {
    $Source = Join-Path $PSScriptRoot '..\artifacts\win-x64\octetledger.exe'
}
$resolvedSource = (Resolve-Path -LiteralPath $Source).Path
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\OctetLedger'
$installedExecutable = Join-Path $installDirectory 'octetledger.exe'

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
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
Write-Host "Open a new terminal, then run: octetledger"
