using System.Globalization;
using System.Text.RegularExpressions;
using OctetLedger.Core;

namespace OctetLedger.Cli;

internal static partial class BudgetCommand
{
    public static int Run(string[] arguments)
    {
        var action = arguments.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        var settings = OctetLedgerSettings.Load();
        switch (action)
        {
            case "set" when arguments.Length == 2:
                if (!TryParseBytes(arguments[1], out var bytes))
                {
                    Console.Error.WriteLine("Budget must be a positive size such as 500GB or 1.5TB.");
                    return 2;
                }
                (settings with { MonthlyBudgetBytes = bytes }).Save();
                Console.WriteLine($"Monthly budget set to {ByteFormatter.Format(bytes)}.");
                return 0;
            case "remove" when arguments.Length == 1:
                (settings with { MonthlyBudgetBytes = null }).Save();
                Console.WriteLine("Monthly budget removed.");
                return 0;
            case "status" when arguments.Length == 1 || arguments.Length == 0:
                return ShowStatus(settings);
            default:
                Console.Error.WriteLine("Usage: octetledger budget [status|set <size>|remove]");
                return 2;
        }
    }

    internal static bool TryParseBytes(string text, out long bytes)
    {
        bytes = 0;
        var match = SizeRegex().Match(text.Trim());
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value <= 0)
            return false;
        var multiplier = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => 1_000m,
            "MB" => 1_000_000m,
            "GB" => 1_000_000_000m,
            "TB" => 1_000_000_000_000m,
            "KIB" => 1_024m,
            "MIB" => 1_048_576m,
            "GIB" => 1_073_741_824m,
            "TIB" => 1_099_511_627_776m,
            _ => 1m
        };
        var total = value * multiplier;
        if (total > long.MaxValue) return false;
        bytes = decimal.ToInt64(decimal.Round(total));
        return true;
    }

    private static int ShowStatus(OctetLedgerSettings settings)
    {
        if (settings.MonthlyBudgetBytes is not long budget)
        {
            Console.WriteLine("No monthly budget configured. Run 'octetledger budget set 500GB'.");
            return 0;
        }
        var now = DateTimeOffset.Now;
        var startLocal = new DateTime(now.Year, now.Month, 1);
        using var store = new TrafficStore();
        var buckets = TrafficReport.SelectInterface(store.ReadBuckets(new DateTimeOffset(startLocal).ToUniversalTime()), null, settings.PreferredInterfaceId);
        var used = buckets.Sum(bucket => bucket.BytesReceived + bucket.BytesSent);
        var elapsedDays = Math.Max(1, now.Day - 1 + now.TimeOfDay.TotalDays);
        var projected = used / elapsedDays * DateTime.DaysInMonth(now.Year, now.Month);
        var percent = budget == 0 ? 0 : used * 100d / budget;
        Console.WriteLine($"Monthly budget: {ByteFormatter.Format(budget)}");
        Console.WriteLine($"Used:           {ByteFormatter.Format(used)} ({percent:0.0}%)");
        Console.WriteLine($"Projected:      {ByteFormatter.Format(projected)}");
        Console.WriteLine($"Remaining:      {ByteFormatter.Format(Math.Max(0, budget - used))}");
        if (percent >= 100) Console.Error.WriteLine("ALERT: monthly network budget exceeded.");
        else if (percent >= 90) Console.Error.WriteLine("ALERT: 90% of the monthly network budget has been used.");
        else if (percent >= 75) Console.Error.WriteLine("Warning: 75% of the monthly network budget has been used.");
        return percent >= 100 ? 1 : 0;
    }

    [GeneratedRegex("^([0-9]+(?:\\.[0-9]+)?)\\s*(B|KB|MB|GB|TB|KIB|MIB|GIB|TIB)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();
}
