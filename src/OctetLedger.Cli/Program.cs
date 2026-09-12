using System.Globalization;
using System.Reflection;
using OctetLedger.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "summary";
var rest = args.Skip(1).ToArray();
return command switch
{
    "summary" => ShowSummary(rest),
    "interfaces" or "iflist" => ShowInterfaces(rest),
    "interface" => ManageInterface(rest),
    "live" => await ShowLiveAsync(rest),
    "collect" => CollectOnce(),
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
    "status" => ShowStatus(),
    "help" or "--help" or "-h" => ShowHelp(),
    "version" or "--version" => ShowVersion(),
    _ => UnknownCommand(command)
};

static int ShowSummary(string[] arguments)
{
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
            new OctetLedgerSettings(match.Id).Save();
            Console.WriteLine($"Default interface set to: {match.Name}");
            return 0;
        case "clear":
            new OctetLedgerSettings().Save();
            Console.WriteLine("Default interface selection is now automatic.");
            return 0;
        default:
            Console.Error.WriteLine("Usage: octetledger interface [show|set <name-or-id>|clear]");
            return 2;
    }
}

static async Task<int> ShowLiveAsync(string[] arguments)
{
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
    return 0;
}

static async Task<int> MonitorAsync(string[] arguments)
{
    var intervalText = ReadOption(arguments, "--interval") ?? "60";
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
    var now = DateTimeOffset.Now;
    int? count;
    DateTimeOffset startLocal;
    switch (kind)
    {
        case ReportKind.Total:
            count = 1; startLocal = DateTimeOffset.UnixEpoch; break;
        case ReportKind.Today:
            count = 1; startLocal = new DateTimeOffset(now.Date, now.Offset); break;
        case ReportKind.Hourly:
            count = ReadPositiveInteger(arguments, "--hours", 24, 1, 744);
            startLocal = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset).AddHours(-(count.GetValueOrDefault() - 1)); break;
        case ReportKind.Daily:
            count = ReadPositiveInteger(arguments, "--days", 30, 1, 3660);
            startLocal = new DateTimeOffset(now.Date, now.Offset).AddDays(-(count.GetValueOrDefault() - 1)); break;
        case ReportKind.Weekly:
            count = ReadPositiveInteger(arguments, "--weeks", 12, 1, 520);
            var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
            startLocal = new DateTimeOffset(monday, now.Offset).AddDays(-7 * (count.GetValueOrDefault() - 1)); break;
        case ReportKind.Monthly:
            count = ReadPositiveInteger(arguments, "--months", 12, 1, 120);
            startLocal = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset).AddMonths(-(count.GetValueOrDefault() - 1)); break;
        default:
            count = ReadPositiveInteger(arguments, "--days", 10, 1, 1000);
            startLocal = new DateTimeOffset(now.Date, now.Offset).AddYears(-10); break;
    }
    if (count is null) return 2;
    using var store = new TrafficStore();
    var buckets = store.ReadBuckets(startLocal.ToUniversalTime());
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
    rows = FilterReportInterfaces(rows, arguments);
    if (HasFlag(arguments, "--json")) { Console.WriteLine(TrafficReportExporter.ToJson(rows)); return 0; }
    var csvPath = ReadOption(arguments, "--csv");
    if (csvPath is not null)
    {
        TrafficReportExporter.WriteCsv(csvPath, rows);
        Console.WriteLine($"CSV saved to: {Path.GetFullPath(csvPath)}");
        return 0;
    }
    PrintReport(rows);
    return 0;
}

static IReadOnlyList<TrafficReportRow> FilterReportInterfaces(IReadOnlyList<TrafficReportRow> rows, string[] arguments)
{
    if (HasFlag(arguments, "--all") || rows.Count == 0) return rows;
    var selector = ReadOption(arguments, "--interface") ?? ReadOption(arguments, "-i");
    string? id;
    if (selector is not null)
    {
        id = rows.FirstOrDefault(row => string.Equals(row.InterfaceId, selector, StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(row.InterfaceName, selector, StringComparison.OrdinalIgnoreCase))?.InterfaceId;
        if (id is null) { Console.Error.WriteLine($"Stored interface '{selector}' was not found in this period."); return []; }
    }
    else
    {
        var preferred = OctetLedgerSettings.Load().PreferredInterfaceId;
        id = rows.Any(row => string.Equals(row.InterfaceId, preferred, StringComparison.OrdinalIgnoreCase))
            ? preferred
            : NetworkInterfaceSelector.SelectPrimary(NetworkInterfaceReader.ReadDistinct(), preferred)?.Id;
        id ??= rows.GroupBy(row => row.InterfaceId).OrderByDescending(group => group.Sum(row => row.TotalBytes)).First().Key;
    }
    return rows.Where(row => string.Equals(row.InterfaceId, id, StringComparison.OrdinalIgnoreCase)).ToArray();
}

static void PrintReport(IReadOnlyList<TrafficReportRow> rows)
{
    Console.WriteLine($"{"Period",16} {"Interface",-24} {"Received",12} {"Sent",12} {"Total",12} {"Average",14} {"Peak",14}");
    Console.WriteLine(new string('-', 112));
    foreach (var row in rows)
        Console.WriteLine($"{row.Period,16} {Trim(row.InterfaceName, 24),-24} {ByteFormatter.Format(row.BytesReceived),12} " +
                          $"{ByteFormatter.Format(row.BytesSent),12} {ByteFormatter.Format(row.TotalBytes),12} " +
                          $"{ByteFormatter.FormatRate(row.AverageBytesPerSecond),14} {ByteFormatter.FormatRate(row.PeakBytesPerSecond),14}");
    var collector = CollectorTaskManager.GetStatus();
    if (rows.Count == 0)
    {
        Console.WriteLine(collector.State == "Running"
            ? "No traffic interval has been recorded yet. The collector is running; try again in about one minute."
            : "No stored traffic yet. Install the collector with 'octetledger collector install'.");
    }
    else if (collector.State != "Running")
    {
        Console.Error.WriteLine($"Warning: stored totals are not updating because the collector is {collector.State.ToLowerInvariant()}.");
        Console.Error.WriteLine("Run 'octetledger collector start' to repair and restart it.");
    }
}

static int ManageCollector(string[] arguments)
{
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
                CollectorTaskManager.Start();
                Console.WriteLine("Automatic collection installed and started (every 60 seconds).");
                return 0;
            case "status":
                var status = CollectorTaskManager.GetStatus(); Console.WriteLine($"Collector: {status.State}"); return status.Installed ? 0 : 1;
            case "start": CollectorTaskManager.Start(); Console.WriteLine("Collector started."); return 0;
            case "stop": CollectorTaskManager.Stop(); Console.WriteLine("Collector stopped."); return 0;
            case "uninstall": CollectorTaskManager.Uninstall(); Console.WriteLine("Automatic collector removed. Stored statistics were preserved."); return 0;
            default: Console.Error.WriteLine("Usage: octetledger collector [install|status|start|stop|uninstall]"); return 2;
        }
    }
    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
    { Console.Error.WriteLine(exception.Message); return 1; }
}

static int ManageDatabase(string[] arguments)
{
    var action = arguments.FirstOrDefault()?.ToLowerInvariant() ?? "check";
    switch (action)
    {
        case "check":
            var checkedPath = arguments.Skip(1).FirstOrDefault() ?? AppDataPaths.DatabasePath;
            var result = TrafficStore.CheckIntegrity(checkedPath);
            Console.WriteLine(result.IsHealthy ? "Database integrity: OK" : "Database integrity: FAILED");
            if (!result.IsHealthy) foreach (var message in result.Messages) Console.WriteLine($"  {message}");
            return result.IsHealthy ? 0 : 1;
        case "backup":
        {
            var destination = arguments.Skip(1).FirstOrDefault() ?? Path.Combine(Environment.CurrentDirectory, $"OctetLedger-backup-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            using var store = new TrafficStore();
            Console.WriteLine($"Database backup created: {store.Backup(destination)}");
            return 0;
        }
        default: Console.Error.WriteLine("Usage: octetledger database [check|backup [path]]"); return 2;
    }
}

static int ShowStatus()
{
    using var store = new TrafficStore();
    var status = store.GetStatus();
    var collector = CollectorTaskManager.GetStatus();
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
    return 0;
}

static int ShowHelp()
{
    Console.WriteLine("""
        OctetLedger — lightweight Windows network traffic statistics

        Usage:
          octetledger [summary] [--interface <name-or-id> | --all]
          octetledger interfaces [--all]
          octetledger interface [show|set <name-or-id>|clear]
          octetledger live [--interface <name-or-id>] [--interval <seconds>]
          octetledger total|today|hourly|daily|weekly|monthly|top [options]
          octetledger collector [install|status|start|stop|uninstall]
          octetledger database [check|backup [path]]
          octetledger status|version|help

        Report options:
          --hours N, --days N, --weeks N, --months N
          --interface <name-or-id>   Choose one stored interface
          --all                      Include every interface (may double-count VPN traffic)
          --json                     Print machine-readable JSON
          --csv <path>               Save CSV data

        'total' shows all traffic recorded since the first stored sample.
        Reports use one primary interface by default to avoid VPN double counting.
        """);
    return 0;
}

static int ShowVersion()
{
    var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
    Console.WriteLine($"OctetLedger {version}"); return 0;
}

static int UnknownCommand(string command) { Console.Error.WriteLine($"Unknown command '{command}'. Run 'octetledger help'."); return 2; }

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

static bool HasFlag(string[] arguments, string flag) => arguments.Contains(flag, StringComparer.OrdinalIgnoreCase);
static string? ReadOption(string[] arguments, string option)
{
    var index = Array.FindIndex(arguments, value => string.Equals(value, option, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}
static int? ReadPositiveInteger(string[] arguments, string option, int defaultValue, int minimum, int maximum)
{
    var text = ReadOption(arguments, option);
    if (text is null) return defaultValue;
    if (int.TryParse(text, out var value) && value >= minimum && value <= maximum) return value;
    Console.Error.WriteLine($"{option} must be between {minimum} and {maximum}."); return null;
}
static string Trim(string value, int maximumLength) => value.Length <= maximumLength ? value : $"{value[..(maximumLength - 1)]}…";

enum ReportKind { Total, Today, Hourly, Daily, Weekly, Monthly, Top }
