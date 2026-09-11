using System.Globalization;

namespace OctetLedger.Core;

public sealed record TrafficReportRow(
    string Period,
    string InterfaceId,
    string InterfaceName,
    long BytesReceived,
    long BytesSent,
    double AverageBytesPerSecond,
    double PeakBytesPerSecond)
{
    public long TotalBytes => BytesReceived + BytesSent;
}

public static class TrafficReport
{
    public static IReadOnlyList<TrafficReportRow> Hourly(IEnumerable<TrafficBucket> buckets)
    {
        return Build(
            buckets,
            bucket => bucket.MinuteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:00", CultureInfo.InvariantCulture),
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
                var peak = group.Max(bucket => (bucket.BytesReceived + bucket.BytesSent) / 60d);
                return new TrafficReportRow(
                    group.Key.Item1,
                    group.Key.InterfaceId,
                    group.Key.InterfaceName,
                    received,
                    sent,
                    (received + sent) / seconds,
                    peak);
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
        var end = start.AddDays(days);
        var now = DateTime.Now;
        var effectiveEnd = end < now ? end : now;
        return Math.Max(1, (effectiveEnd - start).TotalSeconds);
    }
}
