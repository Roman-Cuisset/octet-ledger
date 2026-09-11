using OctetLedger.Core;
using System.Reflection;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "summary";
var exitCode = command switch
{
    "summary" => ShowSummary(),
    "interfaces" or "iflist" => ShowInterfaces(args.Skip(1).ToArray()),
    "live" => await ShowLiveAsync(args.Skip(1).ToArray()),
    "collect" => CollectOnce(),
    "monitor" => await MonitorAsync(args.Skip(1).ToArray()),
    "daily" => ShowDaily(args.Skip(1).ToArray()),
    "monthly" => ShowMonthly(args.Skip(1).ToArray()),
    "status" => ShowStatus(),
    "help" or "--help" or "-h" => ShowHelp(),
    "version" or "--version" => ShowVersion(),
    _ => UnknownCommand(command)
};

return exitCode;

static int ShowSummary()
{
    var interfaces = NetworkInterfaceReader.ReadDistinct()
        .Where(snapshot =>
            snapshot.Status == "Up" &&
            snapshot.Type is not ("Loopback" or "Tunnel") &&
            snapshot.BytesReceived + snapshot.BytesSent > 0)
        .ToArray();

    Console.WriteLine("OctetLedger — Windows network traffic statistics");
    Console.WriteLine();

    if (interfaces.Length == 0)
    {
        Console.Error.WriteLine("No active network interface was found.");
        return 1;
    }

    foreach (var snapshot in interfaces)
    {
        Console.WriteLine(snapshot.Name);
        Console.WriteLine($"  Received  {ByteFormatter.Format(snapshot.BytesReceived),12}");
        Console.WriteLine($"  Sent      {ByteFormatter.Format(snapshot.BytesSent),12}");
        Console.WriteLine($"  Total     {ByteFormatter.Format(snapshot.BytesReceived + snapshot.BytesSent),12}");
    }

    Console.WriteLine();
    Console.WriteLine("Counters shown here are cumulative since Windows started the interface.");
    return 0;
}

static int ShowInterfaces(string[] arguments)
{
    var interfaces = arguments.Contains("--all", StringComparer.OrdinalIgnoreCase)
        ? NetworkInterfaceReader.ReadAll()
        : NetworkInterfaceReader.ReadDistinct();
    Console.WriteLine($"{"Status",-8} {"Name",-28} {"Type",-18} {"Received",12} {"Sent",12}");
    Console.WriteLine(new string('-', 84));

    foreach (var snapshot in interfaces)
    {
        Console.WriteLine(
            $"{snapshot.Status,-8} {Trim(snapshot.Name, 28),-28} {Trim(snapshot.Type, 18),-18} " +
            $"{ByteFormatter.Format(snapshot.BytesReceived),12} {ByteFormatter.Format(snapshot.BytesSent),12}");
    }

    return 0;
}

static async Task<int> ShowLiveAsync(string[] arguments)
{
    var selector = ReadOption(arguments, "--interface") ?? ReadOption(arguments, "-i");
    var intervalText = ReadOption(arguments, "--interval") ?? "1";

    if (!double.TryParse(intervalText, System.Globalization.CultureInfo.InvariantCulture, out var interval) ||
        interval < 0.2 || interval > 60)
    {
        Console.Error.WriteLine("--interval must be between 0.2 and 60 seconds.");
        return 2;
    }

    var first = selector is null
        ? NetworkInterfaceReader.ReadDistinct()
            .Where(snapshot =>
                snapshot.Status == "Up" &&
                snapshot.Type is not ("Loopback" or "Tunnel") &&
                snapshot.BytesReceived + snapshot.BytesSent > 0)
            .OrderByDescending(snapshot => snapshot.Type is "Wireless80211" or "Ethernet")
            .ThenByDescending(snapshot => snapshot.BytesReceived + snapshot.BytesSent)
            .FirstOrDefault()
        : NetworkInterfaceReader.Find(selector);

    if (first is null)
    {
        Console.Error.WriteLine(selector is null
            ? "No active network interface was found."
            : $"Interface '{selector}' was not found. Run 'octetledger interfaces' to list interfaces.");
        return 1;
    }

    Console.WriteLine($"Live traffic on {first.Name}. Press Ctrl+C to stop.");
    Console.WriteLine($"{"Time",10} {"Download",16} {"Upload",16} {"Total",16}");

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    var previous = first;
    try
    {
        while (!cancellation.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellation.Token);
            var current = NetworkInterfaceReader.Find(previous.Id);
            if (current is null)
            {
                Console.Error.WriteLine("The selected interface is no longer available.");
                return 1;
            }

            var rate = TrafficRateCalculator.Calculate(previous, current);
            Console.WriteLine(
                $"{current.CapturedAt:HH:mm:ss} " +
                $"{ByteFormatter.FormatRate(rate.ReceivedBytesPerSecond),16} " +
                $"{ByteFormatter.FormatRate(rate.SentBytesPerSecond),16} " +
                $"{ByteFormatter.FormatRate(rate.TotalBytesPerSecond),16}");
            previous = current;
        }
    }
    catch (OperationCanceledException)
    {
        // Normal Ctrl+C shutdown.
    }

    return 0;
}

static int CollectOnce()
{
    using var store = new TrafficStore();
    var result = store.Collect(NetworkInterfaceReader.ReadDistinct());
    Console.WriteLine(
        $"Collected {result.InterfacesObserved} interfaces: " +
        $"{ByteFormatter.Format(result.BytesReceived)} received, " +
        $"{ByteFormatter.Format(result.BytesSent)} sent.");

    if (result.BaselinesCreated > 0)
    {
        Console.WriteLine($"Initialized {result.BaselinesCreated} new interface baselines.");
    }

    return 0;
}

static async Task<int> MonitorAsync(string[] arguments)
{
    var intervalText = ReadOption(arguments, "--interval") ?? "60";
    if (!double.TryParse(intervalText, System.Globalization.CultureInfo.InvariantCulture, out var interval) ||
        interval < 1 || interval > 3600)
    {
        Console.Error.WriteLine("--interval must be between 1 and 3600 seconds.");
        return 2;
    }

    using var store = new TrafficStore();
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    Console.WriteLine($"Collecting every {interval:0.##} seconds. Press Ctrl+C to stop.");
    try
    {
        while (!cancellation.IsCancellationRequested)
        {
            var result = store.Collect(NetworkInterfaceReader.ReadDistinct());
            Console.WriteLine(
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  " +
                $"↓ {ByteFormatter.Format(result.BytesReceived),10}  " +
                $"↑ {ByteFormatter.Format(result.BytesSent),10}");
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellation.Token);
        }
    }
    catch (OperationCanceledException)
    {
        // Normal Ctrl+C shutdown.
    }

    return 0;
}

static int ShowDaily(string[] arguments)
{
    var days = ReadPositiveInteger(arguments, "--days", 30, 1, 366);
    if (days is null)
    {
        return 2;
    }

    using var store = new TrafficStore();
    var startLocal = DateTimeOffset.Now.Date.AddDays(-(days.Value - 1));
    var buckets = store.ReadBuckets(startLocal.ToUniversalTime());
    var rows = buckets
        .GroupBy(bucket => new
        {
            Day = bucket.MinuteUtc.ToLocalTime().Date,
            bucket.InterfaceId,
            bucket.InterfaceName
        })
        .Select(group => new
        {
            group.Key.Day,
            group.Key.InterfaceName,
            Received = group.Sum(bucket => bucket.BytesReceived),
            Sent = group.Sum(bucket => bucket.BytesSent)
        })
        .OrderByDescending(row => row.Day)
        .ThenBy(row => row.InterfaceName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    Console.WriteLine($"{"Date",12} {"Interface",-24} {"Received",12} {"Sent",12} {"Total",12}");
    Console.WriteLine(new string('-', 78));
    foreach (var row in rows)
    {
        Console.WriteLine(
            $"{row.Day:yyyy-MM-dd}  {Trim(row.InterfaceName, 24),-24} " +
            $"{ByteFormatter.Format(row.Received),12} {ByteFormatter.Format(row.Sent),12} " +
            $"{ByteFormatter.Format(row.Received + row.Sent),12}");
    }

    if (rows.Length == 0)
    {
        Console.WriteLine("No stored traffic yet. Run 'octetledger monitor' to start collecting.");
    }

    return 0;
}

static int ShowMonthly(string[] arguments)
{
    var months = ReadPositiveInteger(arguments, "--months", 12, 1, 120);
    if (months is null)
    {
        return 2;
    }

    var now = DateTimeOffset.Now;
    var startLocal = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset)
        .AddMonths(-(months.Value - 1));
    using var store = new TrafficStore();
    var rows = store.ReadBuckets(startLocal.ToUniversalTime())
        .GroupBy(bucket => new
        {
            Month = bucket.MinuteUtc.ToLocalTime().ToString(
                "yyyy-MM",
                System.Globalization.CultureInfo.InvariantCulture),
            bucket.InterfaceId,
            bucket.InterfaceName
        })
        .Select(group => new
        {
            group.Key.Month,
            group.Key.InterfaceName,
            Received = group.Sum(bucket => bucket.BytesReceived),
            Sent = group.Sum(bucket => bucket.BytesSent)
        })
        .OrderByDescending(row => row.Month, StringComparer.Ordinal)
        .ThenBy(row => row.InterfaceName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    Console.WriteLine($"{"Month",10} {"Interface",-24} {"Received",12} {"Sent",12} {"Total",12}");
    Console.WriteLine(new string('-', 76));
    foreach (var row in rows)
    {
        Console.WriteLine(
            $"{row.Month,10} {Trim(row.InterfaceName, 24),-24} " +
            $"{ByteFormatter.Format(row.Received),12} {ByteFormatter.Format(row.Sent),12} " +
            $"{ByteFormatter.Format(row.Received + row.Sent),12}");
    }

    if (rows.Length == 0)
    {
        Console.WriteLine("No stored traffic yet. Run 'octetledger monitor' to start collecting.");
    }

    return 0;
}

static int ShowStatus()
{
    using var store = new TrafficStore();
    var status = store.GetStatus();
    Console.WriteLine("OctetLedger database");
    Console.WriteLine($"  Path               {status.DatabasePath}");
    Console.WriteLine($"  Size               {ByteFormatter.Format(status.DatabaseBytes)}");
    Console.WriteLine($"  Tracked interfaces {status.TrackedInterfaces}");
    Console.WriteLine($"  Stored minutes     {status.StoredMinutes}");
    Console.WriteLine(
        $"  Last collection    " +
        $"{(status.LastCollectionUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? "never")}");
    return 0;
}

static int ShowHelp()
{
    Console.WriteLine("""
        OctetLedger — lightweight Windows network traffic statistics

        Usage:
          octetledger [summary]
          octetledger interfaces [--all]
          octetledger live [--interface <name-or-id>] [--interval <seconds>]
          octetledger collect
          octetledger monitor [--interval <seconds>]
          octetledger daily [--days <count>]
          octetledger monthly [--months <count>]
          octetledger status
          octetledger version

        Commands:
          summary      Show cumulative counters for active interfaces (default)
          interfaces   List Windows network interfaces (--all includes driver bindings)
          live         Show current download and upload rates
          collect      Save one counter sample to the local database
          monitor      Collect continuously (60-second interval by default)
          daily        Show stored daily totals for each interface
          monthly      Show stored monthly totals for each interface
          status       Show the database path and collection status
          version      Show the application version
        """);
    return 0;
}

static int ShowVersion()
{
    var version = Assembly.GetEntryAssembly()?
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0] ?? "unknown";
    Console.WriteLine($"OctetLedger {version}");
    return 0;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'. Run 'octetledger help'.");
    return 2;
}

static string? ReadOption(string[] arguments, string option)
{
    var index = Array.FindIndex(arguments, value =>
        string.Equals(value, option, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static int? ReadPositiveInteger(
    string[] arguments,
    string option,
    int defaultValue,
    int minimum,
    int maximum)
{
    var text = ReadOption(arguments, option);
    if (text is null)
    {
        return defaultValue;
    }

    if (int.TryParse(text, out var value) && value >= minimum && value <= maximum)
    {
        return value;
    }

    Console.Error.WriteLine($"{option} must be between {minimum} and {maximum}.");
    return null;
}

static string Trim(string value, int maximumLength)
{
    return value.Length <= maximumLength ? value : $"{value[..(maximumLength - 1)]}…";
}
