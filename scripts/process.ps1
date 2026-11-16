# Shared by the packaged installer and installed rollback script (Windows PowerShell 5.1+).
function Invoke-OctetLedgerExecutable {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [string[]]$Arguments = @(),
        [ValidateRange(1, 300)][int]$TimeoutSeconds = 60
    )
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    # Windows CRT quoting, including embedded quotes and backslashes before a closing quote.
    $start.Arguments = (($Arguments | ForEach-Object {
        '"' + (($_ -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
    }) -join ' ')
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    try {
        $null = $process.Start()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $deadline = $TimeoutSeconds * 1000
        $exited = $process.WaitForExit($deadline)
        $remaining = [Math]::Max(1, $deadline - [int]$timer.ElapsedMilliseconds)
        $drained = $exited -and [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($output, $errorOutput), $remaining)
        if (-not $exited -or -not $drained) {
            if (-not $process.HasExited) {
                if ($PSVersionTable.PSEdition -eq 'Core') {
                    $process.Kill($true)
                } else {
                    # .NET Framework lacks Kill(entireProcessTree). Bound taskkill itself as well.
                    $killStart = New-Object Diagnostics.ProcessStartInfo
                    $killStart.FileName = Join-Path ([Environment]::SystemDirectory) 'taskkill.exe'
                    $killStart.Arguments = "/PID $($process.Id) /T /F"
                    $killStart.UseShellExecute = $false
                    $killStart.CreateNoWindow = $true
                    $killer = [Diagnostics.Process]::Start($killStart)
                    try { if (-not $killer.WaitForExit(5000)) { $killer.Kill() } }
                    finally { $killer.Dispose() }
                    if (-not $process.HasExited) { $process.Kill() }
                }
                $null = $process.WaitForExit(5000)
            }
            throw [TimeoutException]::new("'$Executable $($Arguments -join ' ')' did not complete within $TimeoutSeconds seconds.")
        }
        $stdout = $output.GetAwaiter().GetResult()
        $stderr = $errorOutput.GetAwaiter().GetResult()
        $global:LASTEXITCODE = $process.ExitCode
        if ($process.ExitCode -ne 0) {
            throw "'$Executable $($Arguments -join ' ')' failed with exit code $($process.ExitCode). $stderr $stdout"
        }
        return [pscustomobject]@{ Output = $stdout; ErrorOutput = $stderr; ExitCode = $process.ExitCode }
    }
    finally { $process.Dispose() }
}
