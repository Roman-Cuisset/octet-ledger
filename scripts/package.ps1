[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Executable,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Executable)) {
    $Executable = Join-Path $PSScriptRoot '..\artifacts\win-x64\octetledger.exe'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\artifacts\packages'
}

$resolvedExecutable = (Resolve-Path -LiteralPath $Executable).Path
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
$packageName = "OctetLedger-$Version-win-x64"
$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$stagingRoot = Join-Path $temporaryRoot ("octetledger-package-" + [Guid]::NewGuid().ToString('N'))
$packageDirectory = Join-Path $stagingRoot $packageName
$archivePath = Join-Path $resolvedOutput "$packageName.zip"
$checksumPath = "$archivePath.sha256"

try {
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null

    Copy-Item -LiteralPath $resolvedExecutable -Destination (Join-Path $packageDirectory 'octetledger.exe')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1') -Destination $packageDirectory
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.cmd') -Destination $packageDirectory
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination $packageDirectory
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.cmd') -Destination $packageDirectory
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\README.md') -Destination $packageDirectory

    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }
    if (Test-Path -LiteralPath $checksumPath) {
        Remove-Item -LiteralPath $checksumPath -Force
    }

    Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $archivePath
    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $checksumPath -Value "$hash  $packageName.zip" -Encoding Ascii

    Write-Output "Package: $archivePath"
    Write-Output "SHA-256: $hash"
}
finally {
    $resolvedStaging = [System.IO.Path]::GetFullPath($stagingRoot)
    if ($resolvedStaging.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedStaging).StartsWith('octetledger-package-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force -ErrorAction SilentlyContinue
    }
}
