using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace OctetLedger.Core;

public sealed record CollectorTaskStatus(bool Installed, string State, string? Details = null);

public enum CollectorStartupState
{
    Failed,
    Pending,
    Ready
}

internal sealed class CollectorProcessLock : IDisposable
{
    private FileStream? stream;

    private CollectorProcessLock(FileStream stream)
    {
        this.stream = stream;
    }

    public static CollectorProcessLock? TryAcquire(string name)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
        var path = Path.Combine(Path.GetTempPath(), $"octetledger-lock-{key}");
        try
        {
            return new CollectorProcessLock(new FileStream(path, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref stream, null)?.Dispose();
    }
}

public static class CollectorTaskManager
{
    private static string InstallationId => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(AppDataPaths.DataDirectory).ToUpperInvariant())))[..16];
    private static string CollectorLockName => $@"Local\OctetLedgerCollector.{InstallationId}";
    public static string WatchdogTaskName => $"OctetLedger Watchdog {InstallationId}";
    public static string WatchdogPath => Path.Combine(AppDataPaths.DataDirectory, "collector-watchdog.vbs");
    internal static readonly TimeSpan CollectionDelayThreshold = TimeSpan.FromMinutes(OctetLedgerDefaults.DelayedCollectionMinutes);
    private static CollectorProcessLock? collectorLock;
    public static string StartupValueName => $"OctetLedger Collector {InstallationId}";
    public static string LauncherPath => Path.Combine(AppDataPaths.DataDirectory, "collector.vbs");
    public static string PidPath => Path.Combine(AppDataPaths.DataDirectory, "collector.pid");
    public static string ReadyPath => Path.Combine(AppDataPaths.DataDirectory, "collector.ready");
    public static string StopPath => Path.Combine(AppDataPaths.DataDirectory, "collector.stop");
    public static string LogPath => Path.Combine(AppDataPaths.DataDirectory, "collector.log");
    public static string ErrorStatePath => Path.Combine(AppDataPaths.DataDirectory, "collector.errors");

    public static CollectorTaskStatus GetStatus()
    {
        var registered = Run("reg.exe", "query", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", StartupValueName).ExitCode == 0;
        var launcherExists = File.Exists(LauncherPath);
        var watchdogInstalled = File.Exists(WatchdogPath) &&
            Run("schtasks.exe", "/Query", "/TN", WatchdogTaskName).ExitCode == 0;
        using var process = TryGetRunningProcess();
        var running = process is not null || IsCollectorLockHeld();
        var status = DetermineStatus(registered, launcherExists && watchdogInstalled, running,
            ReadLastSuccess(), DateTimeOffset.UtcNow, ReadConsecutiveErrors());
        if (!watchdogInstalled && registered)
            return status with { Details = "Independent watchdog is missing. Run 'octetledger collector install' to repair supervision." };
        return status;
    }

    public static void Install(string executablePath)
    {
        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("OctetLedger executable was not found.", fullPath);
        Directory.CreateDirectory(AppDataPaths.DataDirectory);
        var temporaryLauncher = $"{LauncherPath}.tmp";
        try
        {
            File.WriteAllText(temporaryLauncher, BuildLauncherScript(fullPath, LogPath));
            File.Move(temporaryLauncher, LauncherPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryLauncher)) File.Delete(temporaryLauncher); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        File.WriteAllText(WatchdogPath, BuildWatchdogScript(fullPath));
        var taskFile = Path.Combine(AppDataPaths.DataDirectory, $"watchdog-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(taskFile, BuildWatchdogTaskXml(WatchdogPath, DateTime.UtcNow.AddMinutes(1)), Encoding.Unicode);
            EnsureSuccess(Run("schtasks.exe", "/Create", "/TN", WatchdogTaskName,
                "/XML", taskFile, "/F"), "register collector watchdog");
        }
        finally
        {
            File.Delete(taskFile);
        }
        var result = Run("reg.exe", "add", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", StartupValueName,
            "/t", "REG_SZ", "/d", $"wscript.exe //B //Nologo \"{LauncherPath}\"", "/f");
        EnsureSuccess(result, "register automatic startup");
        var legacy = Run("reg.exe", "query", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", "OctetLedger Collector");
        if (legacy.ExitCode == 0 && legacy.Output.Contains(LauncherPath, StringComparison.OrdinalIgnoreCase))
            Run("reg.exe", "delete", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", "OctetLedger Collector", "/f");
    }

    public static CollectorStartupState Start()
    {
        using var controlLock = CollectorProcessLock.TryAcquire($"OctetLedgerControl.{InstallationId}");
        if (controlLock is null) return CollectorStartupState.Pending;
        var registered = Run("reg.exe", "query", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", StartupValueName).ExitCode == 0;
        if (!File.Exists(LauncherPath) || !File.Exists(WatchdogPath) || !registered ||
            Run("schtasks.exe", "/Query", "/TN", WatchdogTaskName).ExitCode != 0)
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new InvalidOperationException("Collector installation could not be repaired automatically.");
            Install(executable);
        }

        TryDeleteStopFile();
        using var existingProcess = TryGetRunningProcess();
        var lastSuccess = ReadLastSuccess();
        if (existingProcess is not null && lastSuccess is not null &&
            lastSuccess >= existingProcess.StartTime.ToUniversalTime() && DateTimeOffset.UtcNow - lastSuccess <= CollectionDelayThreshold)
            return CollectorStartupState.Ready;
        if (existingProcess is not null && ShouldRestartCollector(lastSuccess,
            existingProcess.StartTime.ToUniversalTime(), DateTimeOffset.UtcNow))
        {
            RecordBackgroundError(new TimeoutException("Watchdog restarting collector: no successful collection for more than three minutes."));
            Stop();
            TryDeleteStopFile();
        }
        if (existingProcess is null || existingProcess.HasExited)
        {
            TryDeleteStopFile();
            Process.Start(new ProcessStartInfo("wscript.exe", $"//B //Nologo \"{LauncherPath}\"") { UseShellExecute = true })?.Dispose();
        }
        var startup = WaitForStartup(() =>
        {
            using var process = TryGetRunningProcess();
            var running = process is not null || IsCollectorLockHeld();
            var success = ReadLastSuccess();
            return (running, process is not null && success is not null &&
                success >= process.StartTime.ToUniversalTime() && DateTimeOffset.UtcNow - success <= CollectionDelayThreshold);
        });
        if (startup is CollectorStartupState.Ready or CollectorStartupState.Pending) return startup;
        throw new InvalidOperationException($"Collector could not be launched. See '{LogPath}' if it was created.");
    }

    internal static bool ShouldRestartCollector(DateTimeOffset? lastSuccess, DateTimeOffset startedUtc, DateTimeOffset nowUtc)
    {
        var observed = lastSuccess is not null && lastSuccess >= startedUtc ? lastSuccess.Value : startedUtc;
        return nowUtc - observed > CollectionDelayThreshold;
    }

    public static void EnsureRunning()
    {
        // A persistent marker distinguishes deliberate stop/update from a crash.
        if (StopRequested) return;
        Start();
    }

    public static void Stop()
    {
        Directory.CreateDirectory(AppDataPaths.DataDirectory);
        File.WriteAllText(StopPath, DateTimeOffset.UtcNow.ToString("O"));
        using var process = TryGetRunningProcess();
        if (process is null)
        {
            if (IsCollectorLockHeld())
            {
                File.WriteAllText(StopPath, DateTimeOffset.UtcNow.ToString("O"));
                for (var attempt = 0; attempt < 150 && IsCollectorLockHeld(); attempt++) Thread.Sleep(100);
                if (IsCollectorLockHeld())
                    throw new InvalidOperationException("Collector did not stop cleanly and its process identity is unavailable. Sign out or restart Windows before retrying.");
            }
            TryDeletePidFile();
            return;
        }
        File.WriteAllText(StopPath, DateTimeOffset.UtcNow.ToString("O"));
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        TryDeletePidFile();
    }

    public static void Uninstall()
    {
        Stop();
        if (Run("schtasks.exe", "/Query", "/TN", WatchdogTaskName).ExitCode == 0)
            EnsureSuccess(Run("schtasks.exe", "/Delete", "/TN", WatchdogTaskName, "/F"), "remove collector watchdog");
        if (Run("reg.exe", "query", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", StartupValueName).ExitCode == 0)
            EnsureSuccess(Run("reg.exe", "delete", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", StartupValueName, "/f"), "remove collector startup");
        if (File.Exists(LauncherPath)) File.Delete(LauncherPath);
        if (File.Exists(WatchdogPath)) File.Delete(WatchdogPath);
        TryDeletePidFile();
        TryDeleteReadyFile();
    }

    public static bool TryClaimBackgroundProcess()
    {
        collectorLock = CollectorProcessLock.TryAcquire(CollectorLockName);
        if (collectorLock is null) return false;
        try
        {
            Directory.CreateDirectory(AppDataPaths.DataDirectory);
            using var current = Process.GetCurrentProcess();
            var executable = Path.GetFullPath(Environment.ProcessPath ?? current.MainModule?.FileName
                ?? throw new InvalidOperationException("Collector executable path is unavailable."));
            File.WriteAllText(PidPath, string.Join('|',
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                executable));
            return true;
        }
        catch
        {
            collectorLock.Dispose();
            collectorLock = null;
            throw;
        }
    }

    public static bool StopRequested => File.Exists(StopPath);

    public static void MarkBackgroundReady()
    {
        var temporary = $"{ReadyPath}.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(temporary, DateTimeOffset.UtcNow.ToString("O"));
            File.Move(temporary, ReadyPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        TryDeleteErrorStateFile();
    }

    public static void RecordBackgroundError(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppDataPaths.DataDirectory);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 256 * 1024)
            {
                File.Move(LogPath, $"{LogPath}.previous", overwrite: true);
            }
            File.AppendAllText(LogPath,
                $"{DateTimeOffset.Now:O} {exception.GetType().Name}: {exception.Message}{Environment.NewLine}");
            var count = ReadConsecutiveErrors() + 1;
            File.WriteAllText(ErrorStatePath, $"{count}|{DateTimeOffset.UtcNow:O}|{exception.GetType().Name}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void ReleaseBackgroundProcess()
    {
        try
        {
            if (File.Exists(PidPath))
            {
                var text = File.ReadAllText(PidPath).Split('|', 2)[0];
                if (int.TryParse(text, out var pid) && pid == Environment.ProcessId) TryDeletePidFile();
            }
        }
        finally
        {
            collectorLock?.Dispose();
            collectorLock = null;
        }
    }

    private static Process? TryGetRunningProcess()
    {
        if (File.Exists(PidPath))
        {
            try
            {
                var identity = File.ReadAllText(PidPath).Split('|', 3);
                if (int.TryParse(identity[0], out var pid))
                {
                    var process = Process.GetProcessById(pid);
                    if (IsExpectedCollectorProcess(process, identity)) return process;
                    process.Dispose();
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            TryDeletePidFile();
        }

        return null;
    }

    private static bool IsExpectedCollectorProcess(Process process, string[] identity)
    {
        try
        {
            if (process.HasExited || !string.Equals(process.ProcessName, "octetledger", StringComparison.OrdinalIgnoreCase))
                return false;
            var currentExecutable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExecutable) ||
                !string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? ""), Path.GetFullPath(currentExecutable), StringComparison.OrdinalIgnoreCase))
                return false;

            if (identity.Length == 3)
            {
                return long.TryParse(identity[1], out var startTicks) &&
                       process.StartTime.ToUniversalTime().Ticks == startTicks &&
                       string.Equals(Path.GetFullPath(identity[2]), Path.GetFullPath(currentExecutable), StringComparison.OrdinalIgnoreCase);
            }

            var pidWritten = File.GetLastWriteTimeUtc(PidPath);
            return Math.Abs((pidWritten - process.StartTime.ToUniversalTime()).TotalMinutes) <= 1;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static bool IsCollectorLockHeld()
    {
        using var processLock = CollectorProcessLock.TryAcquire(CollectorLockName);
        return processLock is null;
    }

    private static DateTimeOffset? ReadLastSuccess()
    {
        try
        {
            if (!File.Exists(ReadyPath)) return null;
            return DateTimeOffset.TryParse(File.ReadAllText(ReadyPath), out var value) ? value.ToUniversalTime() : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static int ReadConsecutiveErrors()
    {
        try
        {
            if (!File.Exists(ErrorStatePath)) return 0;
            var text = File.ReadAllText(ErrorStatePath).Split('|', 2)[0];
            return int.TryParse(text, out var count) && count > 0 ? count : 0;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    internal static CollectorTaskStatus DetermineStatus(
        bool registered,
        bool launcherExists,
        bool running,
        DateTimeOffset? lastSuccess,
        DateTimeOffset nowUtc,
        int consecutiveErrors = 0)
    {
        var installed = registered && launcherExists;
        if (!running)
        {
            var state = installed ? "Installed, stopped" : registered || launcherExists ? "Installation needs repair" : "Not installed";
            return new CollectorTaskStatus(installed, state);
        }

        if (lastSuccess is null)
            return new CollectorTaskStatus(installed, "Starting, first collection pending");

        var age = nowUtc - lastSuccess.Value;
        if (age > CollectionDelayThreshold)
            return new CollectorTaskStatus(installed, "Running, collection delayed",
                $"Last successful collection was {Math.Max(0, Math.Floor(age.TotalMinutes)):0} minutes ago." +
                (consecutiveErrors > 0 ? $" {consecutiveErrors} consecutive collection error(s)." : "") +
                $" See '{LogPath}'.");

        if (consecutiveErrors >= 2)
            return new CollectorTaskStatus(installed, "Running, retrying after errors",
                $"{consecutiveErrors} consecutive collection errors; the latest successful collection is still recent. See '{LogPath}'.");

        return new CollectorTaskStatus(installed, installed ? "Running" : "Running, startup repair needed");
    }

    private static void TryDeletePidFile()
    {
        try { if (File.Exists(PidPath)) File.Delete(PidPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteStopFile()
    {
        try { if (File.Exists(StopPath)) File.Delete(StopPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteReadyFile()
    {
        try { if (File.Exists(ReadyPath)) File.Delete(ReadyPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteErrorStateFile()
    {
        try { if (File.Exists(ErrorStatePath)) File.Delete(ErrorStatePath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static CollectorStartupState WaitForStartup(
        Func<(bool Running, bool Ready)> observe,
        Action? wait = null,
        int attempts = 150)
    {
        var observedRunning = false;
        wait ??= () => Thread.Sleep(100);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            wait();
            var observation = observe();
            observedRunning |= observation.Running;
            if (observation.Running && observation.Ready) return CollectorStartupState.Ready;
        }
        return observedRunning ? CollectorStartupState.Pending : CollectorStartupState.Failed;
    }

    internal static string BuildLauncherScript(string executablePath, string logPath)
    {
        var escapedPath = executablePath.Replace("\"", "\"\"", StringComparison.Ordinal);
        var escapedLogPath = logPath.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"""
            Option Explicit
            Dim shell, fileSystem, command, exitCode
            Set shell = CreateObject("Wscript.Shell")
            shell.Environment("Process")("LOCALAPPDATA") = "{Path.GetDirectoryName(AppDataPaths.DataDirectory)!.Replace("\"", "\"\"", StringComparison.Ordinal)}"
            Set fileSystem = CreateObject("Scripting.FileSystemObject")
            command = Chr(34) & "{escapedPath}" & Chr(34) & " monitor --interval {OctetLedgerDefaults.CollectionIntervalSeconds} --quiet --background"
            Dim stopPath
            stopPath = "{StopPath.Replace("\"", "\"\"", StringComparison.Ordinal)}"
            On Error Resume Next
            Do
                If fileSystem.FileExists(stopPath) Then Exit Do
                Err.Clear
                exitCode = shell.Run(command, 0, True)
                If Err.Number <> 0 Then
                    AppendLog "Launcher error " & CStr(Err.Number) & ": " & Err.Description
                    Err.Clear
                    WScript.Sleep 30000
                ElseIf exitCode = 3 Then
                    Exit Do
                Else
                    If fileSystem.FileExists(stopPath) Then Exit Do
                    AppendLog "Collector exited with code " & CStr(exitCode) & "; restarting"
                    WScript.Sleep 5000
                End If
            Loop

            Sub AppendLog(message)
                Dim stream
                Set stream = fileSystem.OpenTextFile("{escapedLogPath}", 8, True)
                stream.WriteLine Now & " " & message
                stream.Close
            End Sub
            """;
    }

    private static string BuildWatchdogScript(string executablePath)
    {
        var escaped = executablePath.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"""
            Option Explicit
            Dim shell
            Set shell = CreateObject("Wscript.Shell")
            shell.Environment("Process")("LOCALAPPDATA") = "{Path.GetDirectoryName(AppDataPaths.DataDirectory)!.Replace("\"", "\"\"", StringComparison.Ordinal)}"
            WScript.Quit shell.Run(Chr(34) & "{escaped}" & Chr(34) & " collector ensure", 0, True)
            """;
    }

    internal static string BuildWatchdogTaskXml(string watchdogPath, DateTime start)
    {
        var user = System.Security.SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        var arguments = System.Security.SecurityElement.Escape($"//B //Nologo \"{watchdogPath}\"");
        return $"""
            <?xml version="1.0" encoding="utf-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <CalendarTrigger>
                  <Repetition><Interval>PT1M</Interval><Duration>P1D</Duration><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>
                  <StartBoundary>{start.ToString("yyyy-MM-ddTHH:mm:ssK", System.Globalization.CultureInfo.InvariantCulture)}</StartBoundary>
                  <Enabled>true</Enabled><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
                </CalendarTrigger>
                <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger>
              </Triggers>
              <Principals><Principal id="User"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <Enabled>true</Enabled><Hidden>true</Hidden><ExecutionTimeLimit>PT2M</ExecutionTimeLimit>
              </Settings>
              <Actions Context="User"><Exec><Command>wscript.exe</Command><Arguments>{arguments}</Arguments></Exec></Actions>
            </Task>
            """;
    }

    private static (int ExitCode, string Output) Run(string fileName, params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(OctetLedgerDefaults.ExternalProcessTimeoutSeconds)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} did not exit within {OctetLedgerDefaults.ExternalProcessTimeoutSeconds} seconds.");
        }
        Task.WaitAll(outputTask, errorTask);
        return (process.ExitCode, outputTask.Result + errorTask.Result);
    }

    private static void EnsureSuccess((int ExitCode, string Output) result, string action)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException($"Unable to {action}: {result.Output.Trim()}");
    }
}
