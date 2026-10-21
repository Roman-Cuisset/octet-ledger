<#
.SYNOPSIS
Validates real elevated TCP and UDP IPv6 application capture using only ::1 traffic.
.DESCRIPTION
Run from an elevated PowerShell 7 terminal with .NET 10 SDK:
  ./scripts/test-windows-ipv6.ps1 -Executable ./artifacts/win-x64/octetledger.exe -EvidencePath ./ipv6-evidence.json
Builds an isolated probe, then runs two separate production 'apps monitor' windows.
Each window generates only one transport, verifies received payloads, checks the actual
application database totals, and confirms the probe is visible in 'apps top'. ETW sees
other applications during these explicit windows, but their rows and the temporary
database are deleted, never exported. Retained evidence contains only probe totals,
version/hash, architecture, boot/session IDs and timestamps. No packets or addresses
other than ::1 are retained. No network settings, installed collector, or tasks change.
An unavailable IPv6 stack, missing elevation or SDK is BLOCKED/UNSUPPORTED, not PASS.
Windows versions that do not emit loopback IPv6 ETW events fail this validation; socket
success alone never counts as successful ETW capture. Exit 0=pass, 1=fail, 2=blocked,
3=unsupported. Evidence is retained only when -EvidencePath is explicitly supplied.
#>
[CmdletBinding()]
param([string]$Executable, [string]$EvidencePath, [switch]$Help)
if ($Help) { Get-Help $PSCommandPath -Full; return }
$ErrorActionPreference = 'Stop'
$evidence = [ordered]@{
    scenario = 'live-ipv6-etw'; status = 'blocked'; startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    results = [System.Collections.Generic.List[object]]::new()
}
$exitCode = 2
$temp = Join-Path ([IO.Path]::GetTempPath()) ('octetledger-ipv6-' + [Guid]::NewGuid().ToString('N'))
$monitor = $null
try {
    if (-not $IsWindows) { $evidence.status = 'unsupported'; $exitCode = 3; throw 'Windows is required.' }
    $evidence.architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    $evidence.bootUtc = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('O')
    $evidence.sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { $admin = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
    finally { $identity.Dispose() }
    if (-not $admin) { throw 'Run this script in an elevated Administrator terminal. It does not request elevation automatically.' }
    if (-not [Net.Sockets.Socket]::OSSupportsIPv6) { $evidence.status = 'unsupported'; $exitCode = 3; throw 'IPv6 socket support is unavailable.' }
    $exe = (Resolve-Path -LiteralPath $Executable).Path
    $evidence.version = (& $exe version) -join ' '
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read executable version.' }
    $evidence.executableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required to build the isolated probe.' }
    New-Item -ItemType Directory -Path $temp | Out-Null
    $probeDirectory = Join-Path $temp 'probe'
    & dotnet publish (Join-Path $PSScriptRoot 'validation/OctetLedger.IPv6Probe.csproj') -c Release -o $probeDirectory --artifacts-path (Join-Path $temp 'build') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Unable to build the isolated IPv6 probe; install .NET 10 SDK.' }
    $probe = Join-Path $probeDirectory 'OctetLedger.IPv6Probe.exe'
    $evidence.status = 'fail'; $exitCode = 1
    foreach ($transport in @('tcp', 'udp')) {
        $data = Join-Path $temp $transport
        New-Item -ItemType Directory -Path $data | Out-Null
        $start = [Diagnostics.ProcessStartInfo]::new($exe)
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('apps', 'monitor', '--seconds', '18', '--data-dir', $data)) { $start.ArgumentList.Add($argument) }
        $monitor = [Diagnostics.Process]::Start($start)
        $outputTask = $monitor.StandardOutput.ReadToEndAsync()
        $errorTask = $monitor.StandardError.ReadToEndAsync()
        # Allow kernel provider activation before generating traffic; success still requires stored ETW bytes.
        Start-Sleep -Seconds 3
        if ($monitor.HasExited) { throw "The $transport ETW monitor exited before traffic generation: $($errorTask.GetAwaiter().GetResult())" }
        $generatedText = (& $probe $transport) -join "`n"
        if ($LASTEXITCODE -ne 0) {
            $probeExit = $LASTEXITCODE
            if ($probeExit -eq 3) { $evidence.status = 'unsupported'; $exitCode = 3 }
            elseif ($probeExit -eq 4) { $evidence.status = 'blocked'; $exitCode = 2 }
            throw "IPv6 $transport loopback traffic could not be generated: $generatedText"
        }
        $generated = $generatedText | ConvertFrom-Json
        if ($generated.payloadBytesReceived -le 0 -or $generated.payloadBytesSent -ne $generated.payloadBytesReceived) {
            throw "The $transport probe did not verify a successful IPv6 payload transfer."
        }
        if (-not $monitor.WaitForExit(30000)) { throw "The $transport ETW monitor did not finish." }
        $null = $outputTask.GetAwaiter().GetResult()
        $captureError = $errorTask.GetAwaiter().GetResult()
        if ($monitor.ExitCode -ne 0) { throw "The $transport ETW monitor failed: $captureError" }
        $monitor.Dispose(); $monitor = $null
        $totalsText = (& $probe totals (Join-Path $data 'octetledger.db') 'OctetLedger.IPv6Probe.exe') -join "`n"
        $totalsExit = $LASTEXITCODE
        $totals = $totalsText | ConvertFrom-Json
        if ($totalsExit -ne 0 -or $totals.bytesReceived -le 0 -or $totals.bytesSent -le 0) {
            throw "No bidirectional $transport IPv6 ETW bytes were stored for the isolated probe. Loopback sockets alone are not a pass; this Windows ETW configuration may not emit the required events."
        }
        $visible = (& $exe apps top --count 10000 --data-dir $data) -join "`n"
        if ($LASTEXITCODE -ne 0 -or $visible -notmatch '(?m)^OctetLedger\.IPv6Probe\.exe\s') {
            throw "The captured $transport IPv6 probe was not visible in the production application report."
        }
        $evidence.results.Add(@{
            transport = $transport; status = 'pass'; generated = $generated; captured = $totals
            attribution = 'dedicated probe process; one transport per isolated capture window'
            consumerVisible = $true; completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        })
    }
    $evidence.status = 'pass'; $exitCode = 0
}
catch { $evidence.reason = $_.Exception.Message; Write-Warning "$($evidence.status.ToUpperInvariant()): $($evidence.reason)" }
finally {
    if ($null -ne $monitor) {
        if (-not $monitor.HasExited -and -not $monitor.WaitForExit(25000)) {
            # Stop only the uniquely named session owned by this fixture before terminating it.
            & logman.exe stop "OctetLedger-Applications-$($monitor.Id)" -ets 2>$null | Out-Null
            $monitor.Kill($true); $monitor.WaitForExit()
        }
        $monitor.Dispose()
    }
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
    $evidence.completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    if ($EvidencePath) {
        $destination = [IO.Path]::GetFullPath($EvidencePath)
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        $evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $destination -Encoding utf8
    }
    $evidence | ConvertTo-Json -Depth 10 | Write-Output
}
exit $exitCode
