using OctetLedger.Core;

namespace OctetLedger.Cli;

internal static class ReportConsoleWriter
{
    public static void Write(
        IReadOnlyList<TrafficReportRow> rows,
        string scope,
        CollectorTaskStatus collector,
        TextWriter output,
        TextWriter error,
        bool showAllInterfaces = false)
    {
        output.WriteLine($"OctetLedger — recorded traffic ({scope})");
        if (showAllInterfaces)
            output.WriteLine("Including all interfaces (VPN/virtual adapters may duplicate transport traffic).");
        var interfaces = rows.Select(row => row.InterfaceName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (interfaces.Length > 1)
            output.WriteLine($"Interfaces: {string.Join(", ", interfaces)}");
        output.WriteLine();
        output.WriteLine($"{"Period",16} {"Interface",-24} {"Received",12} {"Sent",12} {"Total",12} {"Average",14} {"Peak avg",14}");
        output.WriteLine(new string('-', 112));
        foreach (var row in rows)
        {
            output.WriteLine($"{row.Period,16} {Trim(row.InterfaceName, 24),-24} {ByteFormatter.Format(row.BytesReceived),12} " +
                             $"{ByteFormatter.Format(row.BytesSent),12} {ByteFormatter.Format(row.TotalBytes),12} " +
                             $"{ByteFormatter.FormatRate(row.AverageBytesPerSecond),14} {ByteFormatter.FormatRate(row.PeakBytesPerSecond),14}");
        }

        if (rows.Count == 0)
        {
            output.WriteLine($"No stored traffic was found for {scope}.");
            if (collector.State == "Starting, first collection pending")
                output.WriteLine("The collector is running; its first interval is still pending.");
            else if (collector.State == "Portable/manual collection")
                output.WriteLine("Portable data is collected manually. Run 'octetledger collect --data-dir <directory>' periodically or keep 'monitor' running.");
            else if (collector.State is not ("Running" or "Running, collection delayed" or "Running, retrying after errors"))
                output.WriteLine("Automatic collection is not active. Run 'octetledger collector start'.");
        }
        else if (collector.State == "Starting, first collection pending")
        {
            error.WriteLine("Notice: the collector is running and its first collection is still pending.");
            error.WriteLine("Totals will resume automatically; check again in about one minute.");
        }
        else if (collector.State == "Running, collection delayed")
        {
            error.WriteLine($"Warning: {collector.Details}");
            error.WriteLine("The process exists, but successful collection is late; inspect collector.log.");
        }
        else if (collector.State == "Running, retrying after errors")
        {
            error.WriteLine($"Notice: {collector.Details}");
            error.WriteLine("The collector is still running and will retry automatically.");
        }
        else if (collector.State == "Portable/manual collection")
        {
            error.WriteLine("Notice: this portable data directory updates only through explicit 'collect' or foreground 'monitor' commands.");
        }
        else if (collector.State != "Running")
        {
            error.WriteLine($"Warning: stored totals are not updating because the collector is {collector.State.ToLowerInvariant()}.");
            error.WriteLine("Run 'octetledger collector start' to repair and restart it.");
        }

        var longest = rows.MaxBy(row => row.LongestIntervalSeconds);
        if (longest is not null && longest.LongestIntervalSeconds > OctetLedgerDefaults.LongObservationWarningSeconds)
        {
            error.WriteLine($"Notice: longest observed interval was {FormatDuration(longest.LongestIntervalSeconds)} on {longest.InterfaceName} ({longest.Period}).");
            error.WriteLine("Peak avg is averaged over that interval; traffic during collection gaps is assigned when collection resumes.");
        }
    }

    private static string FormatDuration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(seconds);
        if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays}d {duration.Hours}h";
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1) return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        return $"{Math.Max(1, (int)Math.Round(duration.TotalSeconds)):0}s";
    }

    private static string Trim(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : $"{value[..(maximumLength - 1)]}…";
}
