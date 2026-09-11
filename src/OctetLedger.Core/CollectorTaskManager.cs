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
    public static string StopPath => Path.Combine(AppDataPaths.DataDirectory, "collector.stop");

    public static CollectorTaskStatus GetStatus()
    {
        var registered = Run("reg.exe", "query", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", StartupValueName).ExitCode == 0;
        var launcherExists = File.Exists(LauncherPath);
        var installed = registered && launcherExists;
        var running = TryGetRunningProcess() is not null;
        var state = running
            ? installed ? "Running" : "Running, startup repair needed"
            : installed ? "Installed, stopped" : registered || launcherExists ? "Installation needs repair" : "Not installed";
        return new CollectorTaskStatus(installed, state);
    }

    public static void Install(string executablePath)
    {
        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("OctetLedger executable was not found.", fullPath);
        Directory.CreateDirectory(AppDataPaths.DataDirectory);
        var escapedPath = fullPath.Replace("\"", "\"\"", StringComparison.Ordinal);
        File.WriteAllText(LauncherPath,
            $"CreateObject(\"Wscript.Shell\").Run Chr(34) & \"{escapedPath}\" & Chr(34) & \" monitor --interval 60 --quiet --background\", 0, False\r\n");
        var result = Run("reg.exe", "add", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", StartupValueName,
            "/t", "REG_SZ", "/d", $"wscript.exe \"{LauncherPath}\"", "/f");
        EnsureSuccess(result, "register automatic startup");
    }

    public static void Start()
    {
        if (TryGetRunningProcess() is not null) return;
        var registered = Run("reg.exe", "query", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", StartupValueName).ExitCode == 0;
        if (!File.Exists(LauncherPath) || !registered)
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new InvalidOperationException("Collector installation could not be repaired automatically.");
            Install(executable);
        }
        TryDeleteStopFile();
        Process.Start(new ProcessStartInfo("wscript.exe", $"\"{LauncherPath}\"") { UseShellExecute = true });
        for (var attempt = 0; attempt < 50; attempt++)
        {
            Thread.Sleep(100);
            if (TryGetRunningProcess() is not null) return;
        }
        throw new InvalidOperationException("Collector could not be started. Run 'octetledger monitor --interval 60' to see the error.");
    }

    public static void Stop()
    {
        var process = TryGetRunningProcess();
        if (process is null) return;
        File.WriteAllText(StopPath, DateTimeOffset.UtcNow.ToString("O"));
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        TryDeletePidFile();
        TryDeleteStopFile();
    }

    public static void Uninstall()
    {
        Stop();
        Run("reg.exe", "delete", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", StartupValueName, "/f");
        if (File.Exists(LauncherPath)) File.Delete(LauncherPath);
        TryDeletePidFile();
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
        File.WriteAllText(PidPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    public static bool StopRequested => File.Exists(StopPath);

    public static void ReleaseBackgroundProcess()
    {
        if (File.Exists(PidPath))
        {
            var text = File.ReadAllText(PidPath);
            if (int.TryParse(text, out var pid) && pid == Environment.ProcessId) TryDeletePidFile();
        }
        TryDeleteStopFile();
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
