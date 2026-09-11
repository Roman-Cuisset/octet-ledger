using System.Diagnostics;

namespace OctetLedger.Core;

public sealed record CollectorTaskStatus(bool Installed, string State, string? Details = null);

public static class CollectorTaskManager
{
    public const string StartupValueName = "OctetLedger Collector";
    public static string LauncherPath => Path.Combine(AppDataPaths.DataDirectory, "collector.vbs");
    public static string PidPath => Path.Combine(AppDataPaths.DataDirectory, "collector.pid");

    public static CollectorTaskStatus GetStatus()
    {
        var installed = Run("reg.exe", "query", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", StartupValueName).ExitCode == 0;
        var running = TryGetRunningProcess() is not null;
        return new CollectorTaskStatus(installed, running ? "Running" : installed ? "Installed, stopped" : "Not installed");
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
        if (!File.Exists(LauncherPath)) throw new InvalidOperationException("Collector is not installed.");
        Process.Start(new ProcessStartInfo("wscript.exe", $"\"{LauncherPath}\"") { UseShellExecute = true });
    }

    public static void Stop()
    {
        var process = TryGetRunningProcess();
        if (process is null) return;
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
        TryDeletePidFile();
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
        if (TryGetRunningProcess() is not null) return false;
        Directory.CreateDirectory(AppDataPaths.DataDirectory);
        File.WriteAllText(PidPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    public static void ReleaseBackgroundProcess()
    {
        if (!File.Exists(PidPath)) return;
        var text = File.ReadAllText(PidPath);
        if (int.TryParse(text, out var pid) && pid == Environment.ProcessId) TryDeletePidFile();
    }

    private static Process? TryGetRunningProcess()
    {
        if (!File.Exists(PidPath)) return null;
        try
        {
            if (!int.TryParse(File.ReadAllText(PidPath), out var pid)) { TryDeletePidFile(); return null; }
            var process = Process.GetProcessById(pid);
            if (!process.HasExited && string.Equals(process.ProcessName, "octetledger", StringComparison.OrdinalIgnoreCase)) return process;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        TryDeletePidFile();
        return null;
    }

    private static void TryDeletePidFile()
    {
        try { if (File.Exists(PidPath)) File.Delete(PidPath); }
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
