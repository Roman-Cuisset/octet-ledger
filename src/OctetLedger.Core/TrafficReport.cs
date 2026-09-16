using System.Globalization;

namespace OctetLedger.Core;

public sealed record TrafficReportRow(
    string Period,
    string InterfaceId,
    string InterfaceName,
    long BytesReceived,
    long BytesSent,
    double AverageBytesPerSecond,
    double PeakBytesPerSecond,
    double LongestIntervalSeconds = 60,
    string InterfaceDescription = "",
    string InterfaceType = "")
{
    public long TotalBytes => BytesReceived + BytesSent;
}

public static class TrafficReport
{
    public static IReadOnlyList<TrafficReportRow> Total(IEnumerable<TrafficBucket> buckets)
    {
        var values = buckets.ToArray();
        return Build(
            values,
            _ => "all-time",
            group => Math.Max(1, (DateTimeOffset.UtcNow - group.Min(bucket => bucket.MinuteUtc)).TotalSeconds));
    }

    public static IReadOnlyList<TrafficReportRow> Hourly(IEnumerable<TrafficBucket> buckets)
    {
        return Build(
            buckets,
            bucket => bucket.MinuteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:00 zzz", CultureInfo.InvariantCulture),
            _ => 3600);
    }

    public static IReadOnlyList<TrafficReportRow> Daily(IEnumerable<TrafficBucket> buckets)
    {
        return Build(
            buckets,
            bucket => bucket.MinuteUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            group => SecondsForDay(group.First().MinuteUtc.ToLocalTime().Date));
    }

    public static IReadOnlyList<TrafficReportRow> Weekly(IEnumerable<TrafficBucket> buckets)
    {
        return Build(
            buckets,
            bucket => StartOfWeek(bucket.MinuteUtc.ToLocalTime().Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            group => SecondsForRange(StartOfWeek(group.First().MinuteUtc.ToLocalTime().Date), 7));
    }

    public static IReadOnlyList<TrafficReportRow> Monthly(IEnumerable<TrafficBucket> buckets)
    {
        return Build(
            buckets,
            bucket => bucket.MinuteUtc.ToLocalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture),
            group =>
            {
                var local = group.First().MinuteUtc.ToLocalTime();
                var start = new DateTime(local.Year, local.Month, 1);
                return SecondsForRange(start, DateTime.DaysInMonth(local.Year, local.Month));
            });
    }

    public static IReadOnlyList<TrafficReportRow> TopDays(IEnumerable<TrafficBucket> buckets, int count)
    {
        return Daily(buckets)
            .OrderByDescending(row => row.TotalBytes)
            .Take(count)
            .ToArray();
    }

    public static IReadOnlyList<TrafficBucket> SelectInterface(
        IEnumerable<TrafficBucket> buckets,
        string? selector,
        string? preferredInterfaceId)
    {
        var values = buckets.ToArray();
        if (values.Length == 0) return values;

        if (selector is not null)
        {
            var selectedId = values.FirstOrDefault(bucket =>
                string.Equals(bucket.InterfaceId, selector, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(bucket.InterfaceName, selector, StringComparison.OrdinalIgnoreCase))?.InterfaceId;
            return selectedId is null
                ? []
                : values.Where(bucket => string.Equals(bucket.InterfaceId, selectedId, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        if (values.Any(bucket => string.Equals(bucket.InterfaceId, preferredInterfaceId, StringComparison.OrdinalIgnoreCase)))
            return values.Where(bucket => string.Equals(bucket.InterfaceId, preferredInterfaceId, StringComparison.OrdinalIgnoreCase)).ToArray();

        var physical = values.Where(bucket => NetworkInterfaceSelector.IsLikelyPhysical(
            bucket.InterfaceName, bucket.InterfaceDescription, bucket.InterfaceType)).ToArray();
        if (physical.Length > 0) return physical;

        var fallbackId = values.GroupBy(bucket => bucket.InterfaceId)
            .OrderByDescending(group => group.Sum(bucket => bucket.BytesReceived + bucket.BytesSent))
            .First().Key;
        return values.Where(bucket => string.Equals(bucket.InterfaceId, fallbackId, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public static IReadOnlyList<TrafficReportRow> SelectInterfaceRows(
        IEnumerable<TrafficReportRow> rows,
        string? selector,
        string? preferredInterfaceId)
    {
        var values = rows.ToArray();
        if (values.Length == 0) return values;
        if (selector is not null)
        {
            var selectedId = values.FirstOrDefault(row =>
                string.Equals(row.InterfaceId, selector, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(row.InterfaceName, selector, StringComparison.OrdinalIgnoreCase))?.InterfaceId;
            return selectedId is null
                ? []
                : values.Where(row => string.Equals(row.InterfaceId, selectedId, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        if (values.Any(row => string.Equals(row.InterfaceId, preferredInterfaceId, StringComparison.OrdinalIgnoreCase)))
            return values.Where(row => string.Equals(row.InterfaceId, preferredInterfaceId, StringComparison.OrdinalIgnoreCase)).ToArray();

        var physical = values.Where(row => NetworkInterfaceSelector.IsLikelyPhysical(
            row.InterfaceName, row.InterfaceDescription, row.InterfaceType)).ToArray();
        return physical.Length > 0
            ? physical
            : [values.OrderByDescending(row => row.TotalBytes).First()];
    }

    private static TrafficReportRow[] Build(
        IEnumerable<TrafficBucket> buckets,
        Func<TrafficBucket, string> periodSelector,
        Func<IGrouping<(string Period, string InterfaceId, string InterfaceName), TrafficBucket>, double> secondsSelector)
    {
        return buckets
            .GroupBy(bucket => (periodSelector(bucket), bucket.InterfaceId, bucket.InterfaceName))
            .Select(group =>
            {
                var received = group.Sum(bucket => bucket.BytesReceived);
                var sent = group.Sum(bucket => bucket.BytesSent);
                var seconds = Math.Max(1, secondsSelector(group));
                var peak = group.Max(bucket => bucket.PeakBytesPerSecond ??
                    (bucket.BytesReceived + bucket.BytesSent) / Math.Max(1, bucket.IntervalSeconds));
                var first = group.First();
                return new TrafficReportRow(
                    group.Key.Item1,
                    group.Key.InterfaceId,
                    group.Key.InterfaceName,
                    received,
                    sent,
                    (received + sent) / seconds,
                    peak,
                    group.Max(bucket => bucket.LongestIntervalSeconds ?? bucket.IntervalSeconds),
                    first.InterfaceDescription,
                    first.InterfaceType);
            })
            .OrderByDescending(row => row.Period, StringComparer.Ordinal)
            .ThenBy(row => row.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysSinceMonday);
    }

    private static double SecondsForDay(DateTime day)
    {
        return SecondsForRange(day, 1);
    }

    private static double SecondsForRange(DateTime start, int days)
    {
        return SecondsForRange(start, days, TimeZoneInfo.Local, DateTimeOffset.UtcNow);
    }

    internal static double SecondsForRange(DateTime start, int days, TimeZoneInfo timeZone, DateTimeOffset nowUtc)
    {
        var localStart = DateTime.SpecifyKind(start, DateTimeKind.Unspecified);
        var localEnd = DateTime.SpecifyKind(start.AddDays(days), DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone);
        var effectiveEnd = endUtc < nowUtc.UtcDateTime ? endUtc : nowUtc.UtcDateTime;
        return Math.Max(1, (effectiveEnd - startUtc).TotalSeconds);
    }
}
