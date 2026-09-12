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
    private static CollectorProcessLock? collectorLock;
    public const string StartupValueName = "OctetLedger Collector";
    public static string LauncherPath => Path.Combine(AppDataPaths.DataDirectory, "collector.vbs");
    public static string PidPath => Path.Combine(AppDataPaths.DataDirectory, "collector.pid");
    public static string ReadyPath => Path.Combine(AppDataPaths.DataDirectory, "collector.ready");
    public static string StopPath => Path.Combine(AppDataPaths.DataDirectory, "collector.stop");
    public static string LogPath => Path.Combine(AppDataPaths.DataDirectory, "collector.log");

    public static CollectorTaskStatus GetStatus()
    {
        var registered = Run("reg.exe", "query", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", StartupValueName).ExitCode == 0;
        var launcherExists = File.Exists(LauncherPath);
        var installed = registered && launcherExists;
        using var process = TryGetRunningProcess();
        var running = process is not null;
        var state = running
            ? !File.Exists(ReadyPath) ? "Starting, first collection pending"
                : installed ? "Running" : "Running, startup repair needed"
            : installed ? "Installed, stopped" : registered || launcherExists ? "Installation needs repair" : "Not installed";
        return new CollectorTaskStatus(installed, state);
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
        if (existingProcess is not null && File.Exists(ReadyPath)) return CollectorStartupState.Ready;
        if (existingProcess is null)
        {
            TryDeleteStopFile();
            TryDeleteReadyFile();
            Process.Start(new ProcessStartInfo("wscript.exe", $"//B //Nologo \"{LauncherPath}\"") { UseShellExecute = true });
        }
        var startup = WaitForStartup(() =>
        {
            using var process = TryGetRunningProcess();
            return (process is not null, File.Exists(ReadyPath));
        });
        if (startup is CollectorStartupState.Ready or CollectorStartupState.Pending) return startup;
        throw new InvalidOperationException($"Collector could not be launched. See '{LogPath}' if it was created.");
    }

    public static void Stop()
    {
        using var process = TryGetRunningProcess();
        if (process is null)
        {
            TryDeletePidFile();
            TryDeleteReadyFile();
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
            File.WriteAllText(PidPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
        if (!File.Exists(ReadyPath))
        {
            File.WriteAllText(ReadyPath, DateTimeOffset.UtcNow.ToString("O"));
        }
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
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void ReleaseBackgroundProcess()
    {
        if (File.Exists(PidPath))
        {
            var text = File.ReadAllText(PidPath);
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
                if (int.TryParse(File.ReadAllText(PidPath), out var pid))
                {
                    var process = Process.GetProcessById(pid);
                    if (!process.HasExited && string.Equals(process.ProcessName, "octetledger", StringComparison.OrdinalIgnoreCase)) return process;
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            TryDeletePidFile();
        }

        var currentExecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutable)) return null;
        foreach (var candidate in Process.GetProcessesByName("octetledger"))
        {
            try
            {
                if (candidate.Id != Environment.ProcessId &&
                    string.Equals(candidate.MainModule?.FileName, currentExecutable, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
            candidate.Dispose();
        }
        return null;
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
