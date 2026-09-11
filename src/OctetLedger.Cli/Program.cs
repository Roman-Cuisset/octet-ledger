using OctetLedger.Core;
using System.Reflection;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "summary";
var exitCode = command switch
{
    "summary" => ShowSummary(),
    "interfaces" or "iflist" => ShowInterfaces(args.Skip(1).ToArray()),
    "live" => await ShowLiveAsync(args.Skip(1).ToArray()),
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

static int ShowHelp()
{
    Console.WriteLine("""
        OctetLedger — lightweight Windows network traffic statistics

        Usage:
          octetledger [summary]
          octetledger interfaces [--all]
          octetledger live [--interface <name-or-id>] [--interval <seconds>]
          octetledger version

        Commands:
          summary      Show cumulative counters for active interfaces (default)
          interfaces   List Windows network interfaces (--all includes driver bindings)
          live         Show current download and upload rates
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

static string Trim(string value, int maximumLength)
{
    return value.Length <= maximumLength ? value : $"{value[..(maximumLength - 1)]}…";
}
