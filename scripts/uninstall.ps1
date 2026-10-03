[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\OctetLedger'
$installedExecutable = Join-Path $installDirectory 'octetledger.exe'
if (Test-Path -LiteralPath $installedExecutable) {
    & $installedExecutable collector uninstall 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'The automatic collector could not be removed. The installation and PATH entry were preserved.'
    }
}
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($null -eq $userPath) {
    $userPath = ''
}
$newPathEntries = $userPath -split ';' | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne $installDirectory
}

[Environment]::SetEnvironmentVariable('Path', ($newPathEntries -join ';'), 'User')

if (Test-Path -LiteralPath $installDirectory) {
    Remove-Item -LiteralPath $installDirectory -Recurse -Force
}

Write-Host 'OctetLedger was removed. Open a new terminal to refresh PATH.'
