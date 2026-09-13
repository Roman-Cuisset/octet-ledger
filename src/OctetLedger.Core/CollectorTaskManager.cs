using System.Diagnostics;

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
    private Semaphore? semaphore;

    private CollectorProcessLock(Semaphore semaphore)
    {
        this.semaphore = semaphore;
    }

    public static CollectorProcessLock? TryAcquire(string name)
    {
        var candidate = new Semaphore(1, 1, name);
        try
        {
            if (!candidate.WaitOne(0))
            {
                candidate.Dispose();
                return null;
            }
            return new CollectorProcessLock(candidate);
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        var acquired = Interlocked.Exchange(ref semaphore, null);
        if (acquired is null) return;
        try
        {
            acquired.Release();
        }
        finally
        {
            acquired.Dispose();
        }
    }
}

public static class CollectorTaskManager
{
    private const string CollectorLockName = @"Local\OctetLedgerCollector";
    internal static readonly TimeSpan CollectionDelayThreshold = TimeSpan.FromMinutes(3);
    private static CollectorProcessLock? collectorLock;
    public const string StartupValueName = "OctetLedger Collector";
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
        var installed = registered && launcherExists;
        using var process = TryGetRunningProcess();
        var running = process is not null || IsCollectorLockHeld();
        return DetermineStatus(registered, launcherExists, running, ReadLastSuccess(), DateTimeOffset.UtcNow, ReadConsecutiveErrors());
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
        var result = Run("reg.exe", "add", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", StartupValueName,
            "/t", "REG_SZ", "/d", $"wscript.exe //B //Nologo \"{LauncherPath}\"", "/f");
        EnsureSuccess(result, "register automatic startup");
    }

    public static CollectorStartupState Start()
    {
        var registered = Run("reg.exe", "query", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", StartupValueName).ExitCode == 0;
        if (!File.Exists(LauncherPath) || !registered)
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new InvalidOperationException("Collector installation could not be repaired automatically.");
            Install(executable);
        }

        using var existingProcess = TryGetRunningProcess();
        var existingRunning = existingProcess is not null || IsCollectorLockHeld();
        var lastSuccess = ReadLastSuccess();
        if (existingRunning && lastSuccess is not null && DateTimeOffset.UtcNow - lastSuccess <= CollectionDelayThreshold)
            return CollectorStartupState.Ready;
        if (!existingRunning)
        {
            TryDeleteStopFile();
            TryDeleteReadyFile();
            Process.Start(new ProcessStartInfo("wscript.exe", $"//B //Nologo \"{LauncherPath}\"") { UseShellExecute = true });
        }
        var startup = WaitForStartup(() =>
        {
            using var process = TryGetRunningProcess();
            var running = process is not null || IsCollectorLockHeld();
            var success = ReadLastSuccess();
            return (running, success is not null && DateTimeOffset.UtcNow - success <= CollectionDelayThreshold);
        });
        if (startup is CollectorStartupState.Ready or CollectorStartupState.Pending) return startup;
        throw new InvalidOperationException($"Collector could not be launched. See '{LogPath}' if it was created.");
    }

    public static void Stop()
    {
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
            TryDeleteReadyFile();
            TryDeleteErrorStateFile();
            TryDeleteStopFile();
            return;
        }
        File.WriteAllText(StopPath, DateTimeOffset.UtcNow.ToString("O"));
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        TryDeletePidFile();
        TryDeleteReadyFile();
        TryDeleteErrorStateFile();
        TryDeleteStopFile();
    }

    public static void Uninstall()
    {
        Stop();
        Run("reg.exe", "delete", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", StartupValueName, "/f");
        if (File.Exists(LauncherPath)) File.Delete(LauncherPath);
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
            TryDeleteStopFile();
            TryDeleteReadyFile();
            TryDeleteErrorStateFile();
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
        File.WriteAllText(ReadyPath, DateTimeOffset.UtcNow.ToString("O"));
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
        if (File.Exists(PidPath))
        {
            var text = File.ReadAllText(PidPath).Split('|', 2)[0];
            if (int.TryParse(text, out var pid) && pid == Environment.ProcessId) TryDeletePidFile();
        }
        TryDeleteStopFile();
        TryDeleteReadyFile();
        collectorLock?.Dispose();
        collectorLock = null;
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
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var semaphore = Semaphore.OpenExisting(CollectorLockName);
            if (!semaphore.WaitOne(0)) return true;
            semaphore.Release();
            return false;
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
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
            Set fileSystem = CreateObject("Scripting.FileSystemObject")
            command = Chr(34) & "{escapedPath}" & Chr(34) & " monitor --interval 60 --quiet --background"
            On Error Resume Next
            Do
                Err.Clear
                exitCode = shell.Run(command, 0, True)
                If Err.Number <> 0 Then
                    AppendLog "Launcher error " & CStr(Err.Number) & ": " & Err.Description
                    Err.Clear
                    WScript.Sleep 30000
                ElseIf exitCode <> 0 Then
                    AppendLog "Collector exited with code " & CStr(exitCode) & "; restarting"
                    WScript.Sleep 5000
                Else
                    Exit Do
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

    private static (int ExitCode, string Output) Run(string fileName, params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error);
    }

    private static void EnsureSuccess((int ExitCode, string Output) result, string action)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException($"Unable to {action}: {result.Output.Trim()}");
    }
}
