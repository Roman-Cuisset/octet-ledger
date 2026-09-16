using System.Globalization;
using System.Reflection;
using OctetLedger.Core;
using OctetLedger.Cli;
using static OctetLedger.Cli.CommandLineArguments;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var effectiveArgs = args.ToList();
var dataDirectoryIndex = effectiveArgs.FindIndex(value => string.Equals(value, "--data-dir", StringComparison.OrdinalIgnoreCase));
if (dataDirectoryIndex >= 0)
{
    if (dataDirectoryIndex + 1 >= effectiveArgs.Count)
    {
        Console.Error.WriteLine("--data-dir requires a directory path.");
        return 2;
    }
    AppDataPaths.ConfigureDataDirectory(effectiveArgs[dataDirectoryIndex + 1]);
    effectiveArgs.RemoveRange(dataDirectoryIndex, 2);
}
var command = effectiveArgs.FirstOrDefault()?.ToLowerInvariant() ?? "summary";
var rest = effectiveArgs.Skip(1).ToArray();
var exitCode = command switch
{
    "summary" => ShowSummary(rest),
    "interfaces" or "iflist" => ShowInterfaces(rest),
    "interface" => ManageInterface(rest),
    "live" => await ShowLiveAsync(rest),
    "collect" => rest.Length == 0 ? CollectOnce() : UnexpectedArguments("collect"),
    "monitor" => await MonitorAsync(rest),
    "today" => ShowReport(ReportKind.Today, rest),
    "total" => ShowReport(ReportKind.Total, rest),
    "hourly" => ShowReport(ReportKind.Hourly, rest),
    "daily" => ShowReport(ReportKind.Daily, rest),
    "weekly" => ShowReport(ReportKind.Weekly, rest),
    "monthly" => ShowReport(ReportKind.Monthly, rest),
    "top" => ShowReport(ReportKind.Top, rest),
    "collector" => ManageCollector(rest),
    "database" or "db" => ManageDatabase(rest),
    "update" => await UpdateCommand.RunAsync(rest, GetCurrentVersion()),
    "budget" => BudgetCommand.Run(rest),
    "compare" => ComparisonCommand.Run(rest),
    "dashboard" => await DashboardCommand.RunAsync(rest),
    "apps" => await ApplicationTrafficCommand.RunAsync(rest),
    "status" => rest.Length == 0 ? ShowStatus() : UnexpectedArguments("status"),
    "doctor" => DiagnosticsCommand.RunDoctor(GetCurrentVersion(), rest),
    "help" or "--help" or "-h" => ShowHelp(rest),
    "version" or "--version" => DiagnosticsCommand.ShowVersion(GetCurrentVersion(), rest),
    _ => UnknownCommand(command)
};
if (exitCode == 0 && command is "summary" or "status")
    await UpdateCommand.MaybeNotifyAsync(GetCurrentVersion());
return exitCode;

static int ShowSummary(string[] arguments)
{
    if (!ValidateOptions(arguments, ["--all"], ["--interface", "-i"])) return 2;
    if (HasFlag(arguments, "--all") && (ReadOption(arguments, "--interface") is not null || ReadOption(arguments, "-i") is not null))
    { Console.Error.WriteLine("Use either --all or --interface, not both."); return 2; }
    var selected = SelectSnapshots(NetworkInterfaceReader.ReadDistinct(), arguments);
    if (selected is null) return 2;
    Console.WriteLine("OctetLedger — Windows network traffic statistics\n");
    if (selected.Count == 0)
    {
        Console.Error.WriteLine("No active network interface was found.");
        return 1;
    }
    foreach (var snapshot in selected)
    {
        Console.WriteLine(snapshot.Name);
        Console.WriteLine($"  Received  {ByteFormatter.Format(snapshot.BytesReceived),12}");
        Console.WriteLine($"  Sent      {ByteFormatter.Format(snapshot.BytesSent),12}");
        Console.WriteLine($"  Total     {ByteFormatter.Format(snapshot.BytesReceived + snapshot.BytesSent),12}");
    }
    Console.WriteLine("\nCounters are cumulative since Windows started the interface.");
    Console.WriteLine("Use 'octetledger today' for traffic recorded by OctetLedger today.");
    return 0;
}

static int ShowInterfaces(string[] arguments)
{
    if (!ValidateOptions(arguments, ["--all"], [])) return 2;
    var interfaces = HasFlag(arguments, "--all") ? NetworkInterfaceReader.ReadAll() : NetworkInterfaceReader.ReadDistinct();
    var preferred = OctetLedgerSettings.Load().PreferredInterfaceId;
    Console.WriteLine($"{"Default",-7} {"Status",-10} {"Name",-28} {"Type",-18} {"Received",12} {"Sent",12}");
    Console.WriteLine(new string('-', 94));
    foreach (var snapshot in interfaces)
    {
        var marker = string.Equals(snapshot.Id, preferred, StringComparison.OrdinalIgnoreCase) ? "*" : "";
        Console.WriteLine($"{marker,-7} {snapshot.Status,-10} {Trim(snapshot.Name, 28),-28} {Trim(snapshot.Type, 18),-18} " +
                          $"{ByteFormatter.Format(snapshot.BytesReceived),12} {ByteFormatter.Format(snapshot.BytesSent),12}");
    }
    return 0;
}

static int ManageInterface(string[] arguments)
{
    var action = arguments.FirstOrDefault()?.ToLowerInvariant() ?? "show";
    var settings = OctetLedgerSettings.Load();
    var snapshots = NetworkInterfaceReader.ReadDistinct();
    switch (action)
    {
        case "show":
            if (arguments.Length > 1) { Console.Error.WriteLine("Usage: octetledger interface show"); return 2; }
            var selected = NetworkInterfaceSelector.SelectPrimary(snapshots, settings.PreferredInterfaceId);
            if (selected is null) { Console.Error.WriteLine("No active network interface was found."); return 1; }
            Console.WriteLine($"Default interface: {selected.Name}");
            Console.WriteLine(settings.PreferredInterfaceId is null ? "Selection: automatic" : "Selection: saved by user");
            return 0;
        case "set":
            var selector = string.Join(' ', arguments.Skip(1));
            if (string.IsNullOrWhiteSpace(selector)) { Console.Error.WriteLine("Usage: octetledger interface set <name-or-id>"); return 2; }
            var match = NetworkInterfaceSelector.Resolve(snapshots, selector);
            if (match is null) { Console.Error.WriteLine($"Interface '{selector}' was not found."); return 1; }
            (settings with { PreferredInterfaceId = match.Id }).Save();
            Console.WriteLine($"Default interface set to: {match.Name}");
            return 0;
        case "clear":
            if (arguments.Length > 1) { Console.Error.WriteLine("Usage: octetledger interface clear"); return 2; }
            (settings with { PreferredInterfaceId = null }).Save();
            Console.WriteLine("Default interface selection is now automatic.");
            return 0;
        default:
            Console.Error.WriteLine("Usage: octetledger interface [show|set <name-or-id>|clear]");
            return 2;
    }
}

static async Task<int> ShowLiveAsync(string[] arguments)
{
    if (!ValidateOptions(arguments, [], ["--interval", "--interface", "-i"])) return 2;
    var intervalText = ReadOption(arguments, "--interval") ?? "1";
    if (!double.TryParse(intervalText, CultureInfo.InvariantCulture, out var interval) || interval is < 0.2 or > 60)
    { Console.Error.WriteLine("--interval must be between 0.2 and 60 seconds."); return 2; }
    var snapshots = NetworkInterfaceReader.ReadDistinct();
    var selector = ReadOption(arguments, "--interface") ?? ReadOption(arguments, "-i");
    var first = selector is null
        ? NetworkInterfaceSelector.SelectPrimary(snapshots, OctetLedgerSettings.Load().PreferredInterfaceId)
        : NetworkInterfaceSelector.Resolve(snapshots, selector);
    if (first is null)
    {
        Console.Error.WriteLine(selector is null ? "No active network interface was found." : $"Interface '{selector}' was not found.");
        return 1;
    }
    Console.WriteLine($"Live traffic on {first.Name}. Press Ctrl+C to stop.");
    Console.WriteLine($"{"Time",10} {"Download",16} {"Upload",16} {"Total",16}");
    using var cancellation = CreateCancellation();
    var previous = first;
    try
    {
        while (!cancellation.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellation.Token);
            var current = NetworkInterfaceReader.Find(previous.Id);
            if (current is null) { Console.Error.WriteLine("The selected interface is no longer available."); return 1; }
            var rate = TrafficRateCalculator.Calculate(previous, current);
            Console.WriteLine($"{current.CapturedAt:HH:mm:ss} {ByteFormatter.FormatRate(rate.ReceivedBytesPerSecond),16} " +
                              $"{ByteFormatter.FormatRate(rate.SentBytesPerSecond),16} {ByteFormatter.FormatRate(rate.TotalBytesPerSecond),16}");
            previous = current;
        }
    }
    catch (OperationCanceledException) { }
    return 0;
}

static int CollectOnce()
{
    using var store = new TrafficStore();
    var result = store.Collect(NetworkInterfaceReader.ReadDistinct());
    Console.WriteLine($"Collected {result.InterfacesObserved} interfaces: {ByteFormatter.Format(result.BytesReceived)} received, " +
                      $"{ByteFormatter.Format(result.BytesSent)} sent.");
    if (result.BaselinesCreated > 0) Console.WriteLine($"Initialized {result.BaselinesCreated} new interface baselines.");
    AutomaticBackup.RunIfDue(DateTimeOffset.UtcNow);
    return 0;
}

static async Task<int> MonitorAsync(string[] arguments)
{
    if (!ValidateOptions(arguments, ["--quiet", "--background"], ["--interval"])) return 2;
    var intervalText = ReadOption(arguments, "--interval") ?? OctetLedgerDefaults.CollectionIntervalSeconds.ToString(CultureInfo.InvariantCulture);
    if (!double.TryParse(intervalText, CultureInfo.InvariantCulture, out var interval) || interval is < 1 or > 3600)
    { Console.Error.WriteLine("--interval must be between 1 and 3600 seconds."); return 2; }
    var quiet = HasFlag(arguments, "--quiet");
    var background = HasFlag(arguments, "--background");
    var backgroundClaimed = false;
    if (background)
    {
        try
        {
            if (!CollectorTaskManager.TryClaimBackgroundProcess()) return 0;
            backgroundClaimed = true;
        }
        catch (Exception exception)
        {
            CollectorTaskManager.RecordBackgroundError(exception);
            return 1;
        }
    }
    try
    {
        using var cancellation = CreateCancellation();
        if (!quiet) Console.WriteLine($"Collecting every {interval:0.##} seconds. Press Ctrl+C to stop.");
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    using var store = new TrafficStore();
                    var result = store.Collect(NetworkInterfaceReader.ReadDistinct());
                    if (background) CollectorTaskManager.MarkBackgroundReady();
                    if (!quiet) Console.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  ↓ {ByteFormatter.Format(result.BytesReceived),10}  ↑ {ByteFormatter.Format(result.BytesSent),10}");
                    AutomaticBackup.RunIfDue(DateTimeOffset.UtcNow);
                }
                catch (Exception exception) when (background)
                {
                    CollectorTaskManager.RecordBackgroundError(exception);
                }
                var remaining = TimeSpan.FromSeconds(interval);
                while (remaining > TimeSpan.Zero && !cancellation.IsCancellationRequested)
                {
                    if (background && CollectorTaskManager.StopRequested) return 0;
                    var delay = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
                    await Task.Delay(delay, cancellation.Token);
                    remaining -= delay;
                }
            }
        }
        catch (OperationCanceledException) { }
        return 0;
    }
    finally
    {
        if (backgroundClaimed)
        {
            try
            {
                CollectorTaskManager.ReleaseBackgroundProcess();
            }
            catch (Exception exception)
            {
                CollectorTaskManager.RecordBackgroundError(exception);
            }
        }
    }
}

static int ShowReport(ReportKind kind, string[] arguments)
{
    var countOption = kind switch
    {
        ReportKind.Hourly => "--hours",
        ReportKind.Daily or ReportKind.Top => "--days",
        ReportKind.Weekly => "--weeks",
        ReportKind.Monthly => "--months",
        _ => null
    };
    var valueOptions = new List<string> { "--interface", "-i", "--csv" };
    if (countOption is not null) valueOptions.Add(countOption);
    if (!ValidateOptions(arguments, ["--all", "--json"], valueOptions)) return 2;
    if (HasFlag(arguments, "--json") && ReadOption(arguments, "--csv") is not null)
    { Console.Error.WriteLine("Use either --json or --csv, not both."); return 2; }
    if (HasFlag(arguments, "--all") && (ReadOption(arguments, "--interface") is not null || ReadOption(arguments, "-i") is not null))
    { Console.Error.WriteLine("Use either --all or --interface, not both."); return 2; }

    var now = DateTimeOffset.Now;
    int? count;
    DateTimeOffset startUtc;
    switch (kind)
    {
        case ReportKind.Total:
            count = 1; startUtc = DateTimeOffset.UnixEpoch; break;
        case ReportKind.Today:
            count = 1; startUtc = LocalTimeToUtc(now.Date); break;
        case ReportKind.Hourly:
            count = ReadPositiveInteger(arguments, "--hours", 24, 1, 744);
            startUtc = LocalTimeToUtc(new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(-(count.GetValueOrDefault() - 1))); break;
        case ReportKind.Daily:
            count = ReadPositiveInteger(arguments, "--days", 30, 1, 3660);
            startUtc = LocalTimeToUtc(now.Date.AddDays(-(count.GetValueOrDefault() - 1))); break;
        case ReportKind.Weekly:
            count = ReadPositiveInteger(arguments, "--weeks", 12, 1, 520);
            var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
            startUtc = LocalTimeToUtc(monday.AddDays(-7 * (count.GetValueOrDefault() - 1))); break;
        case ReportKind.Monthly:
            count = ReadPositiveInteger(arguments, "--months", 12, 1, 120);
            startUtc = LocalTimeToUtc(new DateTime(now.Year, now.Month, 1).AddMonths(-(count.GetValueOrDefault() - 1))); break;
        default:
            count = ReadPositiveInteger(arguments, "--days", 10, 1, 1000);
            startUtc = LocalTimeToUtc(now.Date.AddYears(-10)); break;
    }
    if (count is null) return 2;
    using var store = new TrafficStore();
    if (kind == ReportKind.Total)
    {
        IReadOnlyList<TrafficReportRow> totalRows = store.ReadTotalReportRows(DateTimeOffset.UtcNow);
        string totalScope;
        if (HasFlag(arguments, "--all"))
        {
            totalScope = "all stored interfaces";
        }
        else
        {
            var selector = ReadOption(arguments, "--interface") ?? ReadOption(arguments, "-i");
            totalRows = TrafficReport.SelectInterfaceRows(totalRows, selector, OctetLedgerSettings.Load().PreferredInterfaceId);
            totalScope = selector is not null
                ? $"interface '{selector}' across all recorded history"
                : totalRows.Select(row => row.InterfaceId).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() > 1
                    ? "physical interfaces across all recorded history"
                    : totalRows.Count > 0 ? $"historical interface '{totalRows[0].InterfaceName}' across all recorded history" : "all recorded history";
        }
        return OutputReport(totalRows, arguments, totalScope);
    }

    var allBuckets = store.ReadBuckets(startUtc);
    IReadOnlyList<TrafficBucket> buckets;
    string scope;
    if (HasFlag(arguments, "--all"))
    {
        buckets = allBuckets;
        scope = "all stored interfaces in the requested period";
    }
    else
    {
        var selector = ReadOption(arguments, "--interface") ?? ReadOption(arguments, "-i");
        buckets = TrafficReport.SelectInterface(allBuckets, selector, OctetLedgerSettings.Load().PreferredInterfaceId);
        scope = selector is not null
            ? $"interface '{selector}' in the requested period"
            : buckets.Select(bucket => bucket.InterfaceId).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() > 1
                ? "physical interfaces in the requested period"
                : buckets.Count > 0 ? $"historical interface '{buckets[0].InterfaceName}' in the requested period" : "the requested period";
    }
    IReadOnlyList<TrafficReportRow> rows = kind switch
    {
        ReportKind.Total => TrafficReport.Total(buckets),
        ReportKind.Today => TrafficReport.Daily(buckets),
        ReportKind.Hourly => TrafficReport.Hourly(buckets),
        ReportKind.Daily => TrafficReport.Daily(buckets),
        ReportKind.Weekly => TrafficReport.Weekly(buckets),
        ReportKind.Monthly => TrafficReport.Monthly(buckets),
        _ => TrafficReport.TopDays(buckets, count.Value)
    };
    return OutputReport(rows, arguments, scope);
}

static int OutputReport(IReadOnlyList<TrafficReportRow> rows, string[] arguments, string scope)
{
    if (HasFlag(arguments, "--json")) { Console.WriteLine(TrafficReportExporter.ToJson(rows)); return 0; }
    var csvPath = ReadOption(arguments, "--csv");
    if (csvPath is not null)
    {
        TrafficReportExporter.WriteCsv(csvPath, rows);
        Console.WriteLine($"CSV saved to: {Path.GetFullPath(csvPath)}");
        return 0;
    }
    PrintReport(rows, scope);
    return 0;
}

static void PrintReport(IReadOnlyList<TrafficReportRow> rows, string scope)
{
    var collector = AppDataPaths.IsPortable
        ? new CollectorTaskStatus(false, "Portable/manual collection")
        : CollectorTaskManager.GetStatus();
    ReportConsoleWriter.Write(rows, scope, collector, Console.Out, Console.Error);
}

static int ManageCollector(string[] arguments)
{
    if (arguments.Length > 1) { Console.Error.WriteLine("Usage: octetledger collector [install|status|start|stop|uninstall]"); return 2; }
    if (AppDataPaths.IsPortable)
    {
        Console.Error.WriteLine("Collector registration cannot be combined with --data-dir. Use 'octetledger collect --data-dir <directory>' or run 'monitor' in the foreground.");
        return 2;
    }
    var action = arguments.FirstOrDefault()?.ToLowerInvariant() ?? "status";
    try
    {
        switch (action)
        {
            case "install":
                var executable = Environment.ProcessPath;
                if (string.IsNullOrEmpty(executable) || !string.Equals(Path.GetFileName(executable), "octetledger.exe", StringComparison.OrdinalIgnoreCase))
                { Console.Error.WriteLine("Install the packaged octetledger.exe first, then run this command."); return 1; }
                CollectorTaskManager.Install(executable);
                var installStartup = CollectorTaskManager.Start();
                Console.WriteLine(installStartup == CollectorStartupState.Ready
                    ? $"Automatic collection installed and started (every {OctetLedgerDefaults.CollectionIntervalSeconds} seconds)."
                    : "Automatic collection installed; first collection is still pending.");
                return 0;
            case "status":
                var status = CollectorTaskManager.GetStatus();
                Console.WriteLine($"Collector: {status.State}");
                if (status.Details is not null) Console.WriteLine($"  {status.Details}");
                return status.Installed ? 0 : 1;
            case "start":
                var startup = CollectorTaskManager.Start();
                Console.WriteLine(startup == CollectorStartupState.Ready
                    ? "Collector started and collecting."
                    : "Collector started; first collection is still pending and will complete automatically.");
                return 0;
            case "stop": CollectorTaskManager.Stop(); Console.WriteLine("Collector stopped."); return 0;
            case "uninstall": CollectorTaskManager.Uninstall(); Console.WriteLine("Automatic collector removed. Stored statistics were preserved."); return 0;
            default: Console.Error.WriteLine("Usage: octetledger collector [install|status|start|stop|uninstall]"); return 2;
        }
    }
    catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
    { Console.Error.WriteLine(exception.Message); return 1; }
}

static int ManageDatabase(string[] arguments)
{
    try
    {
        return ManageDatabaseCore(arguments);
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or
                                      UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or
                                      Microsoft.Data.Sqlite.SqliteException)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static int ManageDatabaseCore(string[] arguments)
{
    var action = arguments.FirstOrDefault()?.ToLowerInvariant() ?? "check";
    if (arguments.Length > 2 || action == "vacuum" && arguments.Length > 1)
    { Console.Error.WriteLine("Usage: octetledger database [check [path]|backup [path]|restore <path>|retention [days]|vacuum]"); return 2; }
    switch (action)
    {
        case "check":
            var checkedPath = arguments.Skip(1).FirstOrDefault() ?? AppDataPaths.DatabasePath;
            var result = TrafficStore.CheckIntegrity(checkedPath);
            Console.WriteLine(result.IsHealthy ? "Database integrity: OK" : "Database integrity: FAILED");
            if (!result.IsHealthy) foreach (var message in result.Messages) Console.WriteLine($"  {message}");
            return result.IsHealthy ? 0 : 1;
        case "backup":
        case "export":
            {
                var destination = arguments.Skip(1).FirstOrDefault() ?? Path.Combine(Environment.CurrentDirectory, $"OctetLedger-backup-{DateTime.Now:yyyyMMdd-HHmmss}.db");
                using var store = new TrafficStore();
                Console.WriteLine($"Database backup created: {store.Backup(destination)}");
                return 0;
            }
        case "restore":
        case "import":
            {
                var source = arguments.Skip(1).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(source))
                { Console.Error.WriteLine("Usage: octetledger database import <path>"); return 2; }
                var collector = AppDataPaths.IsPortable
                    ? new CollectorTaskStatus(false, "Portable/manual collection")
                    : CollectorTaskManager.GetStatus();
                var wasRunning = collector.Installed && (collector.State.StartsWith("Running", StringComparison.Ordinal) ||
                                 collector.State == "Starting, first collection pending");
                if (wasRunning) CollectorTaskManager.Stop();
                DatabaseRestoreResult restored;
                try { restored = TrafficStore.Restore(source); }
                finally { if (wasRunning && collector.Installed) CollectorTaskManager.Start(); }
                Console.WriteLine($"Database restored: {restored.DatabasePath}");
                if (restored.PreviousDatabaseBackupPath is not null)
                    Console.WriteLine($"Previous database preserved: {restored.PreviousDatabaseBackupPath}");
                if (wasRunning && collector.Installed) Console.WriteLine("Collector restarted.");
                return 0;
            }
        case "retention":
            {
                var settings = OctetLedgerSettings.Load();
                var days = settings.RetentionRawDays;
                if (arguments.Length == 2 && (!int.TryParse(arguments[1], out days) || days is < 1 or > 3660))
                { Console.Error.WriteLine("Retention days must be between 1 and 3660."); return 2; }
                using var store = new TrafficStore();
                var retention = store.ApplyRetention(days, DateTimeOffset.UtcNow);
                (settings with { RetentionRawDays = days }).Save();
                Console.WriteLine($"Archived {retention.RawMinutesArchived} raw minute rows into {retention.DailyRowsWritten} daily rows; retaining {days} raw days.");
                return 0;
            }
        case "auto-backup":
            {
                var setting = arguments.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? "status";
                var settings = OctetLedgerSettings.Load();
                if (setting == "status")
                {
                    Console.WriteLine($"Automatic backups: {(settings.AutomaticBackups ? "enabled" : "disabled")}");
                    Console.WriteLine($"Last backup: {settings.LastAutomaticBackupUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "never"}");
                    Console.WriteLine($"Directory: {AutomaticBackup.BackupDirectory}");
                    return 0;
                }
                if (setting is not ("enable" or "disable"))
                { Console.Error.WriteLine("Usage: octetledger database auto-backup [status|enable|disable]"); return 2; }
                (settings with { AutomaticBackups = setting == "enable" }).Save();
                Console.WriteLine($"Automatic daily backups {(setting == "enable" ? "enabled" : "disabled")}.");
                return 0;
            }
        case "vacuum":
            using (var store = new TrafficStore()) store.Vacuum();
            Console.WriteLine("Database vacuum completed.");
            return 0;
        default: Console.Error.WriteLine("Usage: octetledger database [check [path]|backup [path]|restore <path>|retention [days]|vacuum]"); return 2;
    }
}

static int ShowStatus()
{
    using var store = new TrafficStore();
    var status = store.GetStatus();
    var collector = AppDataPaths.IsPortable
        ? new CollectorTaskStatus(false, "Portable/manual collection", "Run 'collect' periodically or keep 'monitor' running with this --data-dir.")
        : CollectorTaskManager.GetStatus();
    var settings = OctetLedgerSettings.Load();
    var selected = NetworkInterfaceSelector.SelectPrimary(NetworkInterfaceReader.ReadDistinct(), settings.PreferredInterfaceId);
    Console.WriteLine("OctetLedger status");
    Console.WriteLine($"  Database           {status.DatabasePath}");
    Console.WriteLine($"  Database size      {ByteFormatter.Format(status.DatabaseBytes)}");
    Console.WriteLine($"  Tracked interfaces {status.TrackedInterfaces}");
    Console.WriteLine($"  Stored minutes     {status.StoredMinutes}");
    Console.WriteLine($"  Last collection    {status.LastCollectionUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "never"}");
    Console.WriteLine($"  Default interface  {selected?.Name ?? "none"}");
    Console.WriteLine($"  Collector          {collector.State}");
    if (collector.Details is not null) Console.WriteLine($"  Collector details  {collector.Details}");
    Console.WriteLine($"  Data mode          {(AppDataPaths.IsPortable ? "portable" : "per-user")}");
    Console.WriteLine($"  Database integrity {(store.CheckIntegrity().IsHealthy ? "healthy" : "FAILED")}");
    Console.WriteLine($"  Raw data retention {settings.RetentionRawDays} days");
    Console.WriteLine($"  Automatic backups  {(settings.AutomaticBackups ? "enabled" : "disabled")}");
    return 0;
}

static int ShowHelp(string[] arguments)
{
    if (arguments.Length > 1)
    {
        Console.Error.WriteLine("Usage: octetledger help [command]");
        return 2;
    }

    var topic = arguments.FirstOrDefault()?.ToLowerInvariant();
    var text = topic switch
    {
        null => """
            OctetLedger — private Windows network usage history

            Start here:
              octetledger collector install     Collect traffic automatically every minute
              octetledger today                 Show today's recorded traffic
              octetledger dashboard             Open the local 30-day dashboard
              octetledger doctor                Diagnose installation and collection

            Reports:
              total, today, hourly, daily, weekly, monthly, top
              Run 'octetledger help reports' for filters and machine-readable output.

            Management:
              interfaces, interface, collector, database, budget, compare
              update, status, version, doctor

            Optional:
              live                            Real-time interface rate
              dashboard                       Read-only local web dashboard
              apps                            Explicit elevated ETW capture by application
              --data-dir <directory>          Isolated portable data

            Run 'octetledger help <command>' for collector, database, reports, update,
            budget, dashboard, or apps.
            """,
        "reports" or "total" or "today" or "hourly" or "daily" or "weekly" or "monthly" or "top" => """
            Usage: octetledger total|today|hourly|daily|weekly|monthly|top [options]

              --hours N, --days N, --weeks N, --months N
              --interface <name-or-id>   Choose one stored interface
              --all                      Separate rows for every interface; VPNs may overlap
              --json                     Stable camel-case JSON output
              --csv <path>               UTF-8 CSV output

            Reports include physical Wi-Fi/Ethernet history by default and exclude VPN/virtual
            adapters to avoid duplicate transport traffic. 'total' covers all recorded history.
            """,
        "collector" => """
            Usage: octetledger collector [install|status|start|stop|uninstall]

            'install' registers per-user automatic collection; no Administrator rights required.
            Stored statistics survive collector removal and application uninstall.
            """,
        "database" or "db" => """
            Usage: octetledger database <action>

              check [path]               Validate SQLite integrity and OctetLedger schema
              export [path]              Create a consistent backup
              import <path>              Validate and atomically restore a backup
              retention [days]           Archive old minutes into daily totals
              vacuum                     Reclaim unused SQLite space
              auto-backup status|enable|disable
            """,
        "update" => """
            Usage: octetledger update [check|install|rollback|status|enable|disable]

            Installation is always explicit. Downloads are size-limited, checksummed, validated,
            and rollback-capable. Automatic checks only print an availability notice.
            """,
        "budget" => """
            Usage: octetledger budget [status|set <size>|remove]

            Examples: octetledger budget set 500GB
                      octetledger compare this-month last-month
            """,
        "dashboard" => """
            Usage: octetledger dashboard [--port N] [--no-open]

            Serves a read-only dashboard on 127.0.0.1 only. It is not exposed to the network.
            """,
        "apps" => """
            Usage: octetledger apps [top [--days N] [--count N]|monitor [--seconds N]]

            Monitoring is opt-in and requires an elevated Administrator terminal. It records
            estimated ETW payload bytes by process name only during explicit monitoring windows.
            These estimates may differ from interface counters and are not billing-grade totals.
            """,
        _ => null
    };
    if (text is null)
    {
        Console.Error.WriteLine($"No help topic for '{topic}'. Run 'octetledger help'.");
        return 2;
    }
    Console.WriteLine(text);
    return 0;
}


static Version GetCurrentVersion()
{
    var value = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
    return Version.TryParse(value, out var version) ? version : new Version(0, 0);
}

static int UnknownCommand(string command) { Console.Error.WriteLine($"Unknown command '{command}'. Run 'octetledger help'."); return 2; }
static int UnexpectedArguments(string command) { Console.Error.WriteLine($"'{command}' does not accept additional arguments."); return 2; }

static IReadOnlyList<NetworkInterfaceSnapshot>? SelectSnapshots(IReadOnlyList<NetworkInterfaceSnapshot> snapshots, string[] arguments)
{
    if (HasFlag(arguments, "--all")) return snapshots.Where(snapshot => snapshot.Status == "Up" && snapshot.Type is not ("Loopback" or "Tunnel")).ToArray();
    var selector = ReadOption(arguments, "--interface") ?? ReadOption(arguments, "-i");
    var match = selector is null ? NetworkInterfaceSelector.SelectPrimary(snapshots, OctetLedgerSettings.Load().PreferredInterfaceId) : NetworkInterfaceSelector.Resolve(snapshots, selector);
    if (selector is not null && match is null) { Console.Error.WriteLine($"Interface '{selector}' was not found."); return null; }
    return match is null ? [] : [match];
}

static CancellationTokenSource CreateCancellation()
{
    var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
    return cancellation;
}

static DateTimeOffset LocalTimeToUtc(DateTime local)
{
    var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
    while (TimeZoneInfo.Local.IsInvalidTime(unspecified)) unspecified = unspecified.AddMinutes(1);
    if (TimeZoneInfo.Local.IsAmbiguousTime(unspecified))
    {
        var offset = TimeZoneInfo.Local.GetAmbiguousTimeOffsets(unspecified).Max();
        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }
    return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, TimeZoneInfo.Local), TimeSpan.Zero);
}

static string Trim(string value, int maximumLength) => value.Length <= maximumLength ? value : $"{value[..(maximumLength - 1)]}…";

enum ReportKind { Total, Today, Hourly, Daily, Weekly, Monthly, Top }
