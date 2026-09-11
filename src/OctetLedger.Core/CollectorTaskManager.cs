using System.Diagnostics;

namespace OctetLedger.Core;

public sealed record CollectorTaskStatus(bool Installed, string State, string? Details = null);

public static class CollectorTaskManager
{
    private const string CollectorMutexName = @"Local\OctetLedgerCollector";
    private static Mutex? collectorMutex;
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
        var escapedPath = fullPath.Replace("\"", "\"\"", StringComparison.Ordinal);
        var temporaryLauncher = $"{LauncherPath}.tmp";
        try
        {
            File.WriteAllText(temporaryLauncher,
                $"CreateObject(\"Wscript.Shell\").Run Chr(34) & \"{escapedPath}\" & Chr(34) & \" monitor --interval 60 --quiet --background\", 0, False\r\n");
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

    public static void Start()
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
        if (existingProcess is not null && File.Exists(ReadyPath)) return;
        if (existingProcess is null)
        {
            TryDeleteStopFile();
            TryDeleteReadyFile();
            Process.Start(new ProcessStartInfo("wscript.exe", $"//B //Nologo \"{LauncherPath}\"") { UseShellExecute = true });
        }
        for (var attempt = 0; attempt < 150; attempt++)
        {
            Thread.Sleep(100);
            using var process = TryGetRunningProcess();
            if (process is not null && File.Exists(ReadyPath)) return;
        }
        File.WriteAllText(StopPath, DateTimeOffset.UtcNow.ToString("O"));
        throw new InvalidOperationException($"Collector started but could not complete its first collection. See '{LogPath}'.");
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
        collectorMutex = new Mutex(true, CollectorMutexName, out var createdNew);
        if (!createdNew)
        {
            collectorMutex.Dispose();
            collectorMutex = null;
            return false;
        }
        Directory.CreateDirectory(AppDataPaths.DataDirectory);
        TryDeleteStopFile();
        TryDeleteReadyFile();
        File.WriteAllText(PidPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return true;
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
        collectorMutex?.ReleaseMutex();
        collectorMutex?.Dispose();
        collectorMutex = null;
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
