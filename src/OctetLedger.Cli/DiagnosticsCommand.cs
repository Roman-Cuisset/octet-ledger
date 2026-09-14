using System.Globalization;
using System.Runtime.InteropServices;
using OctetLedger.Core;

namespace OctetLedger.Cli;

internal static class DiagnosticsCommand
{
    public static int ShowVersion(Version version, string[] arguments)
    {
        if (arguments.Length == 0)
        {
            Console.WriteLine($"OctetLedger {version}");
            return 0;
        }
        if (arguments is not ["--verbose"])
        {
            Console.Error.WriteLine("Usage: octetledger version [--verbose]");
            return 2;
        }

        var settings = OctetLedgerSettings.Load();
        var collector = CollectorTaskManager.GetStatus();
        Console.WriteLine($"OctetLedger {version}");
        Console.WriteLine($"Executable:    {Environment.ProcessPath ?? "unknown"}");
        Console.WriteLine($"Architecture:  {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}");
        Console.WriteLine($"Data directory:{AppDataPaths.DataDirectory}");
        Console.WriteLine($"Database:      {AppDataPaths.DatabasePath}");
        Console.WriteLine($"Collector:     {collector.State}");
        Console.WriteLine($"Updates:       {(settings.CheckForUpdates ? "enabled" : "disabled")}");
        Console.WriteLine($"Latest known:  {settings.LatestKnownVersion ?? "unknown"}");
        return 0;
    }

    public static int RunDoctor(Version version, string[] arguments)
    {
        if (arguments.Length != 0)
        {
            Console.Error.WriteLine("Usage: octetledger doctor");
            return 2;
        }

        var failures = 0;
        Console.WriteLine($"OctetLedger doctor ({version})");
        Check("Executable", Environment.ProcessPath is { Length: > 0 } executable && File.Exists(executable), Environment.ProcessPath ?? "unavailable", ref failures);

        var pathMatches = FindExecutablesOnPath("octetledger.exe");
        Check("PATH", pathMatches.Count > 0, pathMatches.Count == 0 ? "octetledger.exe not found" : string.Join("; ", pathMatches), ref failures);
        if (pathMatches.Count > 1)
        {
            Console.WriteLine($"WARN  Duplicate PATH entries: {pathMatches.Count} OctetLedger executables found");
        }

        try
        {
            Directory.CreateDirectory(AppDataPaths.DataDirectory);
            var probe = Path.Combine(AppDataPaths.DataDirectory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            Check("Data directory", true, AppDataPaths.DataDirectory, ref failures);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Check("Data directory", false, exception.Message, ref failures);
        }

        if (File.Exists(AppDataPaths.DatabasePath))
        {
            var integrity = TrafficStore.CheckIntegrity(AppDataPaths.DatabasePath);
            Check("Database", integrity.IsHealthy, integrity.IsHealthy ? "integrity OK" : string.Join("; ", integrity.Messages), ref failures);
        }
        else
        {
            Console.WriteLine("WARN  Database: not created yet");
        }

        var interfaces = NetworkInterfaceReader.ReadDistinct().Count(snapshot => snapshot.Status == "Up");
        Check("Network", interfaces > 0, $"{interfaces.ToString(CultureInfo.InvariantCulture)} active interface(s)", ref failures);
        var collector = CollectorTaskManager.GetStatus();
        Check("Collector", collector.Installed, collector.Details is null ? collector.State : $"{collector.State}: {collector.Details}", ref failures);

        Console.WriteLine(failures == 0 ? "Result: healthy" : $"Result: {failures} problem(s) found");
        return failures == 0 ? 0 : 1;
    }

    internal static IReadOnlyList<string> FindExecutablesOnPath(string fileName)
    {
        var results = new List<string>();
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(entry.Trim('"'), fileName));
                if (File.Exists(candidate) && !results.Contains(candidate, StringComparer.OrdinalIgnoreCase)) results.Add(candidate);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return results;
    }

    private static void Check(string name, bool success, string details, ref int failures)
    {
        Console.WriteLine($"{(success ? "OK  " : "FAIL")}  {name}: {details}");
        if (!success) failures++;
    }
}
