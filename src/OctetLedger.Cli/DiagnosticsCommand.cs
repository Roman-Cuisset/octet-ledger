using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OctetLedger.Core;

namespace OctetLedger.Cli;

internal static class DiagnosticsCommand
{
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new() { WriteIndented = true };

    public static int RunDiagnostics(Version version, string[] arguments) =>
        RunDiagnostics(arguments, () => CaptureDiagnosticReport(version), Console.Out, Console.Error);

    internal static int RunDiagnostics(string[] arguments, Func<string> capture, TextWriter output, TextWriter error)
    {
        var preview = arguments.Length == 0 || arguments is ["preview"];
        if (!preview && arguments is not ["export", _])
        {
            error.WriteLine("Usage: octetledger diagnostics [preview|export <path>]");
            return 2;
        }
        try
        {
            if (!preview && !IsLocalExportPath(arguments[1]))
            {
                error.WriteLine("Diagnostics exports require a local drive; network destinations are not supported.");
                return 2;
            }
            var report = capture();
            if (preview)
            {
                // Preview is exactly the JSON payload used by export. A new invocation takes a new snapshot.
                output.Write(report);
                return 0;
            }
            using var stream = new FileStream(arguments[1], FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(report);
            output.WriteLine("Diagnostics exported locally. No data was sent over the network.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Exception messages and destination paths may contain private user or machine information.
            error.WriteLine($"Diagnostics could not be written ({exception.GetType().Name}); existing files are never overwritten.");
            return 1;
        }
    }
    internal static bool IsLocalExportPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return false;
        var root = Path.GetPathRoot(Path.GetFullPath(path))!;
        var driveType = new DriveInfo(root).DriveType;
        return driveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram;
    }


    private static string CaptureDiagnosticReport(Version version)
    {
        var now = DateTimeOffset.UtcNow;
        var portable = AppDataPaths.IsPortable;
        CollectorTaskStatus collector;
        try
        {
            collector = portable
                ? new CollectorTaskStatus(false, "Portable/manual collection")
                : CollectorTaskManager.GetStatus();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        {
            collector = new CollectorTaskStatus(false, "Unavailable");
        }
        var database = File.Exists(AppDataPaths.DatabasePath) ? TrafficStore.CheckIntegrity(AppDataPaths.DatabasePath) : null;
        return SerializeDiagnosticReport(version, now, collector, portable,
            !portable && CollectorTaskManager.StopRequested, database,
            CollectorTaskManager.ReadEvents(DateTimeOffset.MinValue, now));
    }

    internal static string SerializeDiagnosticReport(Version version, DateTimeOffset snapshotUtc,
        CollectorTaskStatus collector, bool portable, bool stopRequested, DatabaseCheckResult? database,
        IReadOnlyList<CollectorEvent> events)
    {
        var timeline = events.Select(item => new
        {
            item.TimestampUtc,
            item.LastTimestampUtc,
            Kind = SafeEventKind(item.Kind),
            item.Count,
            ErrorType = SafeErrorType(item.ErrorType)
        }).ToArray();
        var report = new
        {
            FormatVersion = 1,
            SnapshotUtc = snapshotUtc,
            ApplicationVersion = version.ToString(),
            Platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "Other",
            OsVersion = Environment.OSVersion.Version.ToString(),
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Collection = new
            {
                Mode = portable ? "portable_manual" : "per_user",
                collector.Installed,
                State = SafeCollectorState(collector.State),
                collector.WatchdogInstalled,
                StopRequested = stopRequested,
                collector.ConsecutiveErrors
            },
            Database = new
            {
                SupportedSchemaVersion = TrafficStore.CurrentSchemaVersion,
                Integrity = database is null ? "not_created" : database.IsHealthy ? "healthy" : "failed"
            },
            EventHistory = new
            {
                Coverage = "bounded_retained_history_not_lifetime",
                CountMeaning = "group_count_covers_full_first_to_last_timestamp_span",
                ErrorCounts = timeline.Where(item => item.Kind is "error" or "launcher_error" or "collector_exit")
                    .GroupBy(item => item.ErrorType ?? "OtherError")
                    .Select(group => new { Type = group.Key, Count = group.Sum(item => item.Count) }).ToArray(),
                Events = timeline
            },
            Privacy = new
            {
                NetworkTransfer = false,
                IncludesRawTraffic = false,
                IncludesAdapterIdentifiers = false,
                IncludesHostOrUserIdentifiers = false,
                IncludesPathsOrSettings = false,
                IncludesRawLogsOrExceptionMessages = false
            }
        };
        return JsonSerializer.Serialize(report, DiagnosticJsonOptions) + Environment.NewLine;
    }

    private static string SafeCollectorState(string state) => state switch
    {
        "Portable/manual collection" or "Unavailable" or "Installed, stopped" or "Installation needs repair" or
        "Not installed" or "Starting, first collection pending" or "Running, collection delayed" or
        "Running, retrying after errors" or "Running" or "Running, startup repair needed" => state,
        _ => "Unknown"
    };

    private static string SafeEventKind(string kind) => kind switch
    {
        "started" or "stopped" or "stop_requested" or "watchdog_stale_restart" or "error" or
        "recovery" or "launcher_error" or "collector_exit" => kind,
        _ => "other"
    };

    private static string? SafeErrorType(string? type) => type switch
    {
        null => null,
        "IOException" or "UnauthorizedAccessException" or "InvalidOperationException" or "TimeoutException" or
        "SqliteException" or "Win32Exception" or "NetworkInformationException" or "ArgumentException" or
        "InvalidDataException" or "ObjectDisposedException" or "LauncherError" or "CollectorExit" => type,
        _ => "OtherError"
    };

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
        var collector = AppDataPaths.IsPortable
            ? new CollectorTaskStatus(false, "Portable/manual collection")
            : CollectorTaskManager.GetStatus();
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

        var databaseHealthy = false;
        if (File.Exists(AppDataPaths.DatabasePath))
        {
            var integrity = TrafficStore.CheckIntegrity(AppDataPaths.DatabasePath);
            databaseHealthy = integrity.IsHealthy;
            Check("Database", integrity.IsHealthy, integrity.IsHealthy ? "integrity OK" : string.Join("; ", integrity.Messages), ref failures);
        }
        else
        {
            Console.WriteLine("WARN  Database: not created yet");
        }

        var activeInterfaces = NetworkInterfaceReader.ReadDistinct().Where(snapshot => snapshot.Status == "Up").ToArray();
        var physicalCount = activeInterfaces.Count(snapshot => NetworkInterfaceSelector.IsLikelyPhysical(snapshot.Name, snapshot.Description, snapshot.Type));
        Check("Network", activeInterfaces.Length > 0, $"{activeInterfaces.Length.ToString(CultureInfo.InvariantCulture)} active interface(s), {physicalCount} physical", ref failures);
        foreach (var iface in activeInterfaces.Where(snapshot => NetworkInterfaceSelector.IsLikelyPhysical(snapshot.Name, snapshot.Description, snapshot.Type)))
            Console.WriteLine($"      {iface.Name} ({iface.Type}): {ByteFormatter.Format(iface.BytesReceived)} recv, {ByteFormatter.Format(iface.BytesSent)} sent [Windows counter]");

        if (AppDataPaths.IsPortable)
            Check("Collection mode", true, "portable; use explicit collect or foreground monitor commands", ref failures);
        else
        {
            var collector = CollectorTaskManager.GetStatus();
            var collecting = collector.Installed && collector.State is "Running" or "Starting, first collection pending";
            Check("Collector", collecting, collector.Details is null ? collector.State : $"{collector.State}: {collector.Details}", ref failures);
            if (collector.State == "Starting, first collection pending")
                Console.WriteLine("WARN  First collection is still pending; check again in about one minute.");
        }

        if (databaseHealthy)
        {
            using var store = new TrafficStore();
            var storeStatus = store.GetStatus();
            Console.WriteLine($"INFO  Last collection: {storeStatus.LastCollectionUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "never"}");
            Console.WriteLine($"INFO  Tracked interfaces: {storeStatus.TrackedInterfaces}, stored minutes: {storeStatus.StoredMinutes}");
            var stored = store.ReadTotalReportRows(DateTimeOffset.UtcNow);
            const long significantThreshold = 1_048_576; // 1 MiB
            var significant = stored.Where(row => row.TotalBytes >= significantThreshold ||
                NetworkInterfaceSelector.IsLikelyPhysical(row.InterfaceName, row.InterfaceDescription, row.InterfaceType)).ToArray();
            var hidden = stored.Count - significant.Length;
            foreach (var row in significant)
                Console.WriteLine($"      {row.InterfaceName}: {ByteFormatter.Format(row.BytesReceived)} recv, {ByteFormatter.Format(row.BytesSent)} sent [OctetLedger history]");
            if (hidden > 0)
                Console.WriteLine($"      ({hidden} nonphysical interface(s) with < 1 MiB total hidden)");
            if (stored.Count > 0 && activeInterfaces.Length > 0)
                Console.WriteLine("INFO  Windows counters and OctetLedger history cover different time windows.");
        }

        var settings = OctetLedgerSettings.Load();
        if (settings.PreferredInterfaceId is not null)
        {
            var preferredName = activeInterfaces.FirstOrDefault(snapshot =>
                string.Equals(snapshot.Id, settings.PreferredInterfaceId, StringComparison.OrdinalIgnoreCase))?.Name ?? settings.PreferredInterfaceId;
            Console.WriteLine($"INFO  Default interface: {preferredName} (saved by user)");
        }
        else
            Console.WriteLine("INFO  Default interface: automatic selection");

        Console.WriteLine(failures == 0 ? "Result: healthy" : $"Result: {failures} problem(s) found");
        if (failures > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Tips: 'octetledger status' shows stored vs live interface data.");
            Console.WriteLine("      'octetledger daily --all' shows all stored interfaces.");
            Console.WriteLine("      'octetledger daily --interface <name>' filters one adapter.");
        }
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
