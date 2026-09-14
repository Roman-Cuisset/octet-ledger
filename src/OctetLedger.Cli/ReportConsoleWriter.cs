using OctetLedger.Core;

namespace OctetLedger.Cli;

internal static class ReportConsoleWriter
{
    public static void Write(
        IReadOnlyList<TrafficReportRow> rows,
        string scope,
        CollectorTaskStatus collector,
        TextWriter output,
        TextWriter error)
    {
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

        if (rows.Any(row => row.LongestIntervalSeconds > OctetLedgerDefaults.LongObservationWarningSeconds))
            error.WriteLine($"Notice: Peak avg is the highest average over an observed interval; one or more intervals exceeded {OctetLedgerDefaults.LongObservationWarningSeconds} seconds.");
    }

    private static string Trim(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : $"{value[..(maximumLength - 1)]}…";
}
