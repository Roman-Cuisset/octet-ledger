using OctetLedger.Core;

namespace OctetLedger.Cli;

internal static class ComparisonCommand
{
    public static int Run(string[] arguments)
    {
        if (arguments.Length != 2)
        {
            Console.Error.WriteLine("Usage: octetledger compare <today|this-month> <yesterday|last-month>");
            return 2;
        }
        var now = DateTimeOffset.Now;
        (DateTime FirstStart, DateTime FirstEnd, DateTime SecondStart, DateTime SecondEnd, string FirstName, string SecondName) range;
        if (arguments[0].Equals("today", StringComparison.OrdinalIgnoreCase) && arguments[1].Equals("yesterday", StringComparison.OrdinalIgnoreCase))
            range = (now.Date, now.Date.AddDays(1), now.Date.AddDays(-1), now.Date, "today", "yesterday");
        else if (arguments[0].Equals("this-month", StringComparison.OrdinalIgnoreCase) && arguments[1].Equals("last-month", StringComparison.OrdinalIgnoreCase))
        {
            var month = new DateTime(now.Year, now.Month, 1);
            range = (month, month.AddMonths(1), month.AddMonths(-1), month, "this month", "last month");
        }
        else
        {
            Console.Error.WriteLine("Supported comparisons: today yesterday; this-month last-month.");
            return 2;
        }

        using var store = new TrafficStore();
        var settings = OctetLedgerSettings.Load();
        var buckets = TrafficReport.SelectInterface(store.ReadBuckets(new DateTimeOffset(range.SecondStart).ToUniversalTime()), null, settings.PreferredInterfaceId);
        var first = Sum(buckets, range.FirstStart, range.FirstEnd);
        var second = Sum(buckets, range.SecondStart, range.SecondEnd);
        var change = second == 0 ? (first == 0 ? 0 : 100) : (first - second) * 100d / second;
        Console.WriteLine($"{range.FirstName,-12} {ByteFormatter.Format(first),12}");
        Console.WriteLine($"{range.SecondName,-12} {ByteFormatter.Format(second),12}");
        Console.WriteLine($"Change       {change:+0.0;-0.0;0.0}%");
        if (range.FirstName == "this month")
        {
            var elapsed = Math.Max(1, now.Day - 1 + now.TimeOfDay.TotalDays);
            var projected = first / elapsed * DateTime.DaysInMonth(now.Year, now.Month);
            Console.WriteLine($"Projection   {ByteFormatter.Format(projected),12}");
        }
        return 0;
    }

    private static long Sum(IEnumerable<TrafficBucket> buckets, DateTime localStart, DateTime localEnd) => buckets
        .Where(bucket => bucket.MinuteUtc.ToLocalTime().DateTime >= localStart && bucket.MinuteUtc.ToLocalTime().DateTime < localEnd)
        .Sum(bucket => bucket.BytesReceived + bucket.BytesSent);
}
