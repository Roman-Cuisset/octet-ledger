<#
.SYNOPSIS
Prepare/verify actual operator-initiated Windows adapter, power and reboot transitions.
.DESCRIPTION
PowerShell 7 and a published native octetledger.exe are required. This script NEVER
changes adapters, connects VPNs, suspends, hibernates, reboots, elevates, or registers a
collector. Use an otherwise idle test machine. It reads Windows event metadata only.

Discover adapter IDs with [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()
(or octetledger interfaces --all). Example Wi-Fi to Ethernet, initially Wi-Fi Up and
Ethernet Down/disconnected:
  ./scripts/test-windows-transition.ps1 -Phase Prepare -Scenario adapter-switch -Executable ./artifacts/win-x64/octetledger.exe -StateDirectory ./switch-test -FromInterfaceId '{wifi-id}' -ToInterfaceId '{ethernet-id}'
  # Operator connects Ethernet and disconnects Wi-Fi using Windows controls.
  ./scripts/test-windows-transition.ps1 -Phase Verify -StateDirectory ./switch-test
Reverse the IDs for Ethernet to Wi-Fi. VPN scenarios use -Scenario vpn-on or vpn-off
and -InterfaceId '{vpn-adapter-id}'; prepare vpn-on before connecting, prepare vpn-off
while connected. The named adapter must actually change Down/absent <-> Up. VPN
adapters created only on connect require their known stable ID from a prior connection.

For power transitions use -Scenario sleep, hibernate, or reboot with no adapter IDs.
After Prepare returns, the OPERATOR chooses the requested Windows action, waits, then
runs Verify after wake/reboot (same executable must remain at the recorded path).
Sleep/hibernate require matching Kernel-Power event 42 TargetState (4/5) and a later
wake event; reboot requires a changed OS boot timestamp. Merely waiting is PENDING,
never PASS. Missing/unreadable Windows event evidence is BLOCKED. Hibernate/Modern
Standby configurations without the specified events remain unsupported/blocked, not
silently accepted as another scenario. No power configuration is changed.

Prepare stores only a disposable isolated database, local state and version/hash,
boot/session/timestamp evidence in the explicitly selected NEW StateDirectory. It
never reads the user's installed database. Verify collects twice after the event and
asserts report validity/precision, cumulative preservation, active adapter visibility,
and retained elapsed intervals when continuous counters permit that assertion. Actual
traffic may be zero; zero-byte measured intervals must still be reportable. It does
not infer billing accuracy, wake cause, VPN routing, or Internet reachability. IDs are
hashed and interface aliases, IPs, event messages, payloads and traffic totals are NOT
included in retained evidence.json. After pass/fail, only evidence.json is retained.
PENDING/BLOCKED retains the prepared fixture for retry; remove it explicitly with
-Phase Cleanup -StateDirectory <path> if abandoning. Cleanup only accepts a directory
bearing this script's ownership marker. Exit 0=pass/prepared/cleanup, 1=fail, 2=blocked,
3=unsupported, 4=pending. Run separate prepare/verify pairs for every scenario.
#>
[CmdletBinding()]
param(
    [ValidateSet('Prepare', 'Verify', 'Cleanup')][string]$Phase = 'Prepare',
    [ValidateSet('adapter-switch', 'vpn-on', 'vpn-off', 'sleep', 'hibernate', 'reboot')][string]$Scenario,
    [string]$Executable, [string]$StateDirectory,
    [string]$FromInterfaceId, [string]$ToInterfaceId, [string]$InterfaceId,
    [switch]$Help
)
if ($Help) { Get-Help $PSCommandPath -Full; return }
$ErrorActionPreference = 'Stop'
$marker = 'OctetLedger.RealTransition.1'
$exitCode = 2
$evidence = [ordered]@{ status = 'blocked'; phase = $Phase; startedUtc = [DateTimeOffset]::UtcNow.ToString('O') }
$root = $null
$owned = $false
$removeFixture = $false
function Hash-Id([string]$Id) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Id)))
}
function Read-Snapshot {
    $adapters = @([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | ForEach-Object {
        $adapter = $_
        try {
            $statistics = $adapter.GetIPStatistics()
            @{ id = $adapter.Id; type = $adapter.NetworkInterfaceType.ToString(); status = $adapter.OperationalStatus.ToString()
               received = $statistics.BytesReceived; sent = $statistics.BytesSent }
        }
        catch [Net.NetworkInformation.NetworkInformationException] { }
    })
    return @{
        utc = [DateTimeOffset]::UtcNow.ToString('O'); bootUtc = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('O')
        sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId; adapters = $adapters
    }
}
function Read-Adapter($Snapshot, [string]$Id) { return @($Snapshot.adapters | Where-Object { $_.id -eq $Id }) | Select-Object -First 1 }
function Is-Up($Snapshot, [string]$Id) { return (Read-Adapter $Snapshot $Id).status -eq 'Up' }
function Invoke-Collect([string]$Exe, [string]$Data) {
    $output = & $Exe collect --data-dir $Data 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Production isolated collection failed.' }
}
function Read-Report([string]$Exe, [string]$Data) {
    $json = (& $Exe total --all --json --data-dir $Data) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Production isolated report failed.' }
    return @($json | ConvertFrom-Json)
}
function Read-PowerEvents([DateTimeOffset]$Since) {
    # Export only record identity/time/state, never arbitrary event messages or XML.
    $filter = @{ LogName = 'System'; StartTime = $Since.LocalDateTime; Id = @(1, 42, 107) }
    $events = @(Get-WinEvent -FilterHashtable $filter -ErrorAction Stop)
    return @($events | Where-Object {
        ($_.ProviderName -eq 'Microsoft-Windows-Kernel-Power' -and $_.Id -in @(42, 107)) -or
        ($_.ProviderName -eq 'Microsoft-Windows-Power-Troubleshooter' -and $_.Id -eq 1)
    } | ForEach-Object {
        $event = $_; [xml]$xml = $event.ToXml()
        $target = @($xml.Event.EventData.Data | Where-Object { $_.Name -eq 'TargetState' } | ForEach-Object { $_.'#text' }) | Select-Object -First 1
        @{ id = $event.Id; recordId = $event.RecordId; provider = $event.ProviderName
           utc = $event.TimeCreated.ToUniversalTime().ToString('O'); targetState = $target }
    })
}
try {
    if (-not $IsWindows) { $evidence.status = 'unsupported'; $exitCode = 3; throw 'Windows PowerShell 7 is required.' }
    if ([string]::IsNullOrWhiteSpace($StateDirectory)) { throw '-StateDirectory is required.' }
    $root = [IO.Path]::GetFullPath($StateDirectory)
    $stateFile = Join-Path $root 'state.json'
    $markerFile = Join-Path $root '.octetledger-validation'
    $data = Join-Path $root 'fixture'
    if ($Phase -eq 'Prepare') {
        if (Test-Path -LiteralPath $root) { throw 'Prepare requires a NEW StateDirectory; existing directories are never overwritten.' }
        if (-not $Scenario) { throw '-Scenario is required for Prepare.' }
        $exe = (Resolve-Path -LiteralPath $Executable).Path
        $before = Read-Snapshot
        switch ($Scenario) {
            'adapter-switch' {
                if (-not $FromInterfaceId -or -not $ToInterfaceId -or $FromInterfaceId -eq $ToInterfaceId) { throw 'Specify distinct -FromInterfaceId and -ToInterfaceId.' }
                $from = Read-Adapter $before $FromInterfaceId; $to = Read-Adapter $before $ToInterfaceId
                $types = @($from.type, $to.type)
                if ($types -notcontains 'Wireless80211' -or @($types | Where-Object { $_ -like '*Ethernet*' }).Count -eq 0) {
                    $evidence.status = 'unsupported'; $exitCode = 3; throw 'Adapter switch requires one actual Wi-Fi and one Ethernet adapter.'
                }
                $hardware = @(Get-NetAdapter -IncludeHidden | Where-Object { $_.HardwareInterface } |
                    ForEach-Object { $_.InterfaceGuid.ToString().Trim('{}') })
                if ($hardware -notcontains $FromInterfaceId.Trim('{}') -or $hardware -notcontains $ToInterfaceId.Trim('{}')) {
                    $evidence.status = 'unsupported'; $exitCode = 3; throw 'The chosen switch adapters are not both Windows physical hardware interfaces.'
                }
                if (-not (Is-Up $before $FromInterfaceId) -or (Is-Up $before $ToInterfaceId)) { throw 'Prepare with source Up and destination Down/disconnected.' }
            }
            'vpn-on' { if (-not $InterfaceId -or (Is-Up $before $InterfaceId)) { throw 'Specify -InterfaceId and prepare before that VPN adapter becomes Up.' } }
            'vpn-off' { if (-not $InterfaceId -or -not (Is-Up $before $InterfaceId)) { throw 'Specify an Up VPN -InterfaceId before disconnecting.' } }
        }
        $version = (& $exe version) -join ' '
        if ($LASTEXITCODE -ne 0) { throw 'Unable to read executable version.' }
        New-Item -ItemType Directory -Path $root | Out-Null
        $marker | Set-Content -LiteralPath $markerFile -Encoding utf8
        $owned = $true
        $evidence.status = 'fail'; $exitCode = 1; $removeFixture = $true
        Invoke-Collect $exe $data
        Start-Sleep -Seconds 2
        Invoke-Collect $exe $data
        $baseline = Read-Report $exe $data
        $before = Read-Snapshot
        $state = @{
            schema = $marker; scenario = $Scenario; executable = $exe; version = $version
            executableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
            prepared = $before; baseline = $baseline
            fromInterfaceId = $FromInterfaceId; toInterfaceId = $ToInterfaceId; interfaceId = $InterfaceId
        }
        $state | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $stateFile -Encoding utf8
        $evidence.status = 'prepared'; $exitCode = 0; $removeFixture = $false
        $evidence.scenario = $Scenario; $evidence.version = $version; $evidence.bootUtc = $before.bootUtc; $evidence.sessionId = $before.sessionId
        Write-Host 'Prepared. Initiate the requested transition yourself, then run -Phase Verify with this StateDirectory. No transition has passed yet.'
    }
    else {
        if (-not (Test-Path -LiteralPath $markerFile) -or (Get-Content -LiteralPath $markerFile -Raw).Trim() -ne $marker) { throw 'Directory is not owned by this validation script.' }
        $owned = $true
        if ($Phase -eq 'Cleanup') {
            Remove-Item -LiteralPath $root -Recurse -Force
            $owned = $false; $evidence.status = 'cleaned'; $exitCode = 0
        }
        else {
            $state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
            if ($state.schema -ne $marker) { throw 'Unsupported validation state schema.' }
            $exe = $state.executable
            if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne $state.executableSha256) { throw 'Executable changed since Prepare; start a new validation pair.' }
            $after = Read-Snapshot
            $evidence.scenario = $state.scenario; $evidence.version = $state.version; $evidence.executableSha256 = $state.executableSha256
            $evidence.architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
            $evidence.before = @{ utc = $state.prepared.utc; bootUtc = $state.prepared.bootUtc; sessionId = $state.prepared.sessionId }
            $evidence.after = @{ utc = $after.utc; bootUtc = $after.bootUtc; sessionId = $after.sessionId }
            $performed = $false
            switch ($state.scenario) {
                'adapter-switch' { $performed = -not (Is-Up $after $state.fromInterfaceId) -and (Is-Up $after $state.toInterfaceId) }
                'vpn-on' { $performed = -not (Is-Up $state.prepared $state.interfaceId) -and (Is-Up $after $state.interfaceId) }
                'vpn-off' { $performed = (Is-Up $state.prepared $state.interfaceId) -and -not (Is-Up $after $state.interfaceId) }
                'reboot' { $performed = $after.bootUtc -ne $state.prepared.bootUtc }
                { $_ -in @('sleep', 'hibernate') } {
                    try { $events = @(Read-PowerEvents ([DateTimeOffset]::Parse($state.prepared.utc))) }
                    catch {
                        if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') { $events = @() }
                        else { throw 'System power event evidence could not be read; use an account allowed to read the System event log.' }
                    }
                    $target = if ($state.scenario -eq 'hibernate') { '5' } else { '4' }
                    $suspend = @($events | Where-Object { $_.id -eq 42 -and $_.targetState -eq $target } | Sort-Object utc | Select-Object -First 1)
                    $wake = @($events | Where-Object { $_.id -in @(1, 107) -and $suspend.Count -gt 0 -and $_.utc -gt $suspend[0].utc })
                    $performed = $suspend.Count -gt 0 -and $wake.Count -gt 0 -and $after.bootUtc -eq $state.prepared.bootUtc
                    $evidence.powerEvents = $events
                }
            }
            if (-not $performed) { $evidence.status = 'pending'; $exitCode = 4; throw 'The requested physical transition has not been proven. Perform it, or retry after Windows event evidence is available. No pass was recorded.' }
            $evidence.status = 'fail'; $exitCode = 1; $removeFixture = $true
            Invoke-Collect $exe $data
            Start-Sleep -Seconds 2
            Invoke-Collect $exe $data
            $rows = @(Read-Report $exe $data)
            if ($rows.Count -eq 0) { throw 'No measured report rows after the proven transition.' }
            foreach ($row in $rows) {
                if ($row.bytesReceived -lt 0 -or $row.bytesSent -lt 0 -or $row.totalBytes -ne ($row.bytesReceived + $row.bytesSent) -or $row.precision -ne 'raw_intervals') {
                    throw 'Isolated report contains invalid counters or does not explicitly report raw interval precision.'
                }
            }
            foreach ($old in $state.baseline) {
                $current = @($rows | Where-Object { $_.interfaceId -eq $old.interfaceId }) | Select-Object -First 1
                if ($null -eq $current -or $current.bytesReceived -lt $old.bytesReceived -or $current.bytesSent -lt $old.bytesSent) {
                    throw 'Previously measured adapter totals were lost or decreased across the transition.'
                }
            }
            $expectedId = switch ($state.scenario) {
                'adapter-switch' { $state.toInterfaceId }
                'vpn-on' { $state.interfaceId }
                default { $null }
            }
            if ($expectedId -and @($rows | Where-Object { $_.interfaceId -eq $expectedId }).Count -eq 0) {
                throw 'The newly active adapter has no consumer-visible measured interval after two actual collections.'
            }
            $continuous = 0
            $elapsed = ([DateTimeOffset]::Parse($after.utc) - [DateTimeOffset]::Parse($state.prepared.utc)).TotalSeconds
            foreach ($oldAdapter in $state.prepared.adapters) {
                $newAdapter = Read-Adapter $after $oldAdapter.id
                if ($oldAdapter.status -eq 'Up' -and $newAdapter.status -eq 'Up' -and $oldAdapter.type -ne 'Loopback' -and
                    $newAdapter.received -ge $oldAdapter.received -and $newAdapter.sent -ge $oldAdapter.sent) {
                    $current = @($rows | Where-Object { $_.interfaceId -eq $oldAdapter.id }) | Select-Object -First 1
                    # Filter bindings may collapse into another adapter; only assert retained consumer rows.
                    if ($null -ne $current) {
                        if ($current.longestIntervalSeconds -lt [Math]::Max(0, $elapsed - 2)) { throw 'The report discarded elapsed collection interval metadata across the transition.' }
                        $continuous++
                    }
                }
            }
            $evidence.assertions = @('physical-transition-proven', 'production-collection-succeeded-twice', 'valid-raw-precision-report', 'prior-totals-preserved', 'required-active-adapter-visible')
            $evidence.continuousIntervalsChecked = $continuous
            $evidence.adapterStates = @($after.adapters | ForEach-Object { @{ idHash = Hash-Id $_.id; type = $_.type; status = $_.status } })
            $evidence.status = 'pass'; $exitCode = 0
        }
    }
}
catch { $evidence.reason = $_.Exception.Message; Write-Warning "$($evidence.status.ToUpperInvariant()): $($evidence.reason)" }
finally {
    if ($owned) {
        if ($removeFixture) {
            if (Test-Path -LiteralPath (Join-Path $root 'fixture')) { Remove-Item -LiteralPath (Join-Path $root 'fixture') -Recurse -Force }
            if (Test-Path -LiteralPath (Join-Path $root 'state.json')) { Remove-Item -LiteralPath (Join-Path $root 'state.json') -Force }
        }
        $evidence.completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        $evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root 'evidence.json') -Encoding utf8
    }
    $evidence | ConvertTo-Json -Depth 10 | Write-Output
}
exit $exitCode
