using System.Globalization;
using OctetLedger.Core;

namespace OctetLedger.Cli;

internal static class CollectionDisplay
{
    internal static DateTimeOffset? NextExpectedUtc(CollectorTaskStatus collector, DateTimeOffset? lastSuccessUtc) =>
        lastSuccessUtc is not null && collector.State.StartsWith("Running", StringComparison.Ordinal)
            ? lastSuccessUtc.Value.AddSeconds(OctetLedgerDefaults.CollectionIntervalSeconds)
            : null;

    internal static string Timestamp(DateTimeOffset? timestamp) =>
        timestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) ?? "unknown";

    internal static string Precision(string precision) => precision switch
    {
        "daily_archives" => "daily archives",
        "mixed" => "intervals + daily archives",
        _ => "counter intervals"
    };

    internal static string Reason(string reason) => reason switch
    {
        "unknown_collection_gap" => "no successful collection; cause unknown",
        "interface_not_observed" => "interface not observed",
        "interface_not_up" => "interface reported down",
        "counter_reset" => "counter reset; preceding volume cannot be recovered",
        "clock_not_advanced" => "clock did not advance; counters rebaselined",
        "no_active_interfaces" => "no active interface",
        "unknown_coverage" => "coverage unknown (legacy or retained daily detail)",
        _ => reason.Replace('_', ' ')
    };

    internal static void WriteHistory(CollectionHistory history, TextWriter output)
    {
        output.WriteLine("Collection history (collector-wide; independent of traffic interface filters):");
        output.WriteLine($"  Tracking began: {Timestamp(history.TrackingStartedUtc)}");
        output.WriteLine($"  Raw observation detail from: {Timestamp(history.RawHistoryFromUtc)}");
        output.WriteLine("  Earlier or untracked time has unknown coverage, not zero traffic.");
        foreach (var day in history.Days)
            output.WriteLine($"  {day.Period}: {day.Observations} observations, {day.MeasuredIntervals} measured intervals, " +
                             $"{day.ZeroTrafficIntervals} zero-byte intervals, {day.NoActiveInterfaceObservations} no-active-interface observations ({Precision(day.Precision)})");
        if (history.Days.Count == 0) output.WriteLine("  No observation detail in this period.");
        foreach (var gap in history.Gaps)
            output.WriteLine($"  {Timestamp(gap.StartUtc)} -> {(gap.EndUtc is null ? "ongoing" : Timestamp(gap.EndUtc))}: " +
                             $"{Reason(gap.Reason)}{(gap.InterfaceName is null ? "" : $" [{gap.InterfaceName}]")}");
    }
}
