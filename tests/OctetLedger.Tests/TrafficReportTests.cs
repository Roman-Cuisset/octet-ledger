using OctetLedger.Core;

namespace OctetLedger.Tests;

public class TrafficReportTests
{
    [Fact]
    public void TotalCombinesEveryBucketForEachInterface()
    {
        var buckets = new[]
        {
            new TrafficBucket("wifi", "Wi-Fi", DateTimeOffset.UtcNow.AddHours(-2), 1_000, 500),
            new TrafficBucket("wifi", "Wi-Fi", DateTimeOffset.UtcNow.AddHours(-1), 2_000, 750),
            new TrafficBucket("vpn", "VPN", DateTimeOffset.UtcNow.AddHours(-1), 9_000, 3_000)
        };

        var rows = TrafficReport.Total(buckets);

        Assert.Equal(2, rows.Count);
        var wifi = Assert.Single(rows, row => row.InterfaceId == "wifi");
        Assert.Equal("all-time", wifi.Period);
        Assert.Equal(3_000, wifi.BytesReceived);
        Assert.Equal(1_250, wifi.BytesSent);
    }

    [Fact]
    public void DailySeparatesInterfacesAndComputesPeak()
    {
        var now = DateTimeOffset.Now;
        var buckets = new[]
        {
            Bucket("wifi", "Wi-Fi", now, 6_000, 3_000),
            Bucket("wifi", "Wi-Fi", now.AddMinutes(1), 12_000, 6_000),
            Bucket("vpn", "VPN", now, 5_000, 1_000)
        };

        var rows = TrafficReport.Daily(buckets);

        Assert.Equal(2, rows.Count);
        var wifi = rows.Single(row => row.InterfaceId == "wifi");
        Assert.Equal(18_000, wifi.BytesReceived);
        Assert.Equal(9_000, wifi.BytesSent);
        Assert.Equal(300, wifi.PeakBytesPerSecond);
    }

    [Fact]
    public void PeakUsesActualObservationInterval()
    {
        var bucket = new TrafficBucket("wifi", "Wi-Fi", DateTimeOffset.UtcNow, 9_000, 3_000, 600);

        var row = Assert.Single(TrafficReport.Daily([bucket]));

        Assert.Equal(20, row.PeakBytesPerSecond);
        Assert.Equal(600, row.LongestIntervalSeconds);
    }

    [Fact]
    public void InterfaceIsSelectedBeforeTopDaysAreRanked()
    {
        var now = DateTimeOffset.Now;
        var buckets = new[]
        {
            Bucket("vpn", "VPN", now.AddDays(-1), 100_000, 0),
            Bucket("vpn", "VPN", now.AddDays(-2), 90_000, 0),
            Bucket("wifi", "Wi-Fi", now.AddDays(-3), 10_000, 0),
            Bucket("wifi", "Wi-Fi", now.AddDays(-4), 9_000, 0)
        };

        var selected = TrafficReport.SelectInterface(buckets, "Wi-Fi", null);
        var rows = TrafficReport.TopDays(selected, 2);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("wifi", row.InterfaceId));
    }

    [Fact]
    public void AutomaticSelectionUsesAnInterfacePresentInHistoricalData()
    {
        var buckets = new[]
        {
            new TrafficBucket("old-wifi", "Old Wi-Fi", DateTimeOffset.UtcNow, 8_000, 2_000),
            new TrafficBucket("vpn", "VPN", DateTimeOffset.UtcNow, 1_000, 500)
        };

        var selected = TrafficReport.SelectInterface(buckets, null, "currently-active-but-absent");

        Assert.All(selected, bucket => Assert.Equal("old-wifi", bucket.InterfaceId));
    }

    [Fact]
    public void AutomaticSelectionKeepsPhysicalInterfaceChangesAndExcludesVirtualTraffic()
    {
        var now = DateTimeOffset.UtcNow;
        var buckets = new[]
        {
            new TrafficBucket("wifi", "Wi-Fi", now, 1_000, 500, InterfaceDescription: "Intel Wi-Fi", InterfaceType: "Wireless80211"),
            new TrafficBucket("ethernet", "Ethernet", now.AddDays(-1), 200, 4_000, InterfaceDescription: "Realtek PCIe", InterfaceType: "Ethernet"),
            new TrafficBucket("tailscale", "Tailscale", now, 1_000, 4_000, InterfaceDescription: "Tailscale Tunnel", InterfaceType: "53")
        };
        var rows = TrafficReport.Total(buckets);

        var selectedBuckets = TrafficReport.SelectInterface(buckets, null, null);
        var selectedRows = TrafficReport.SelectInterfaceRows(rows, null, null);

        Assert.Equal(["ethernet", "wifi"], selectedBuckets.Select(bucket => bucket.InterfaceId).Distinct().Order().ToArray());
        Assert.Equal(["ethernet", "wifi"], selectedRows.Select(row => row.InterfaceId).Order().ToArray());
    }

    [Fact]
    public void AutomaticRowFallbackKeepsAllPeriodsForTheBusiestInterface()
    {
        var rows = new[]
        {
            new TrafficReportRow("2026-01-02", "vpn", "VPN", 60, 0, 0, 0),
            new TrafficReportRow("2026-01-01", "vpn", "VPN", 60, 0, 0, 0),
            new TrafficReportRow("2026-01-02", "tunnel", "Tunnel", 100, 0, 0, 0)
        };

        var selected = TrafficReport.SelectInterfaceRows(rows, null, null);

        Assert.Equal(["2026-01-02", "2026-01-01"], selected.Select(row => row.Period).ToArray());
        Assert.All(selected, row => Assert.Equal("vpn", row.InterfaceId));
    }

    [Fact]
    public void HourlyDoesNotPresentDailyArchivesAsAnHourOfTraffic()
    {
        var minute = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var raw = new TrafficBucket("wifi", "Wi-Fi", minute, 123, 456);
        var archive = new TrafficBucket("wifi", "Wi-Fi", minute, 100_000, 200_000, IsDailyArchive: true);

        var row = Assert.Single(TrafficReport.Hourly([raw, archive]));

        Assert.Equal(123, row.BytesReceived);
        Assert.Equal(456, row.BytesSent);
    }

    [Fact]
    public void CurrentHourDurationUsesElapsedTimeRatherThanACompleteHour()
    {
        var localMinute = new DateTimeOffset(2026, 1, 1, 12, 20, 0, TimeSpan.FromHours(5.5));
        var nowUtc = new DateTimeOffset(2026, 1, 1, 6, 50, 0, TimeSpan.Zero);

        Assert.Equal(1_200, TrafficReport.SecondsForHour(localMinute, nowUtc));
        Assert.Equal(3_600, TrafficReport.SecondsForHour(localMinute, nowUtc.AddHours(2)));
    }

    [Theory]
    [InlineData(3, 1, 23 * 60 * 60)]
    [InlineData(10, 1, 25 * 60 * 60)]
    public void CalendarDaysHandleInvalidAndRepeatedMidnights(int month, int day, double expectedSeconds)
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 10, 1));
        var zone = TimeZoneInfo.CreateCustomTimeZone("MidnightDST", TimeSpan.Zero,
            "MidnightDST", "Standard", "Daylight", [rule]);
        var start = new DateTime(2026, month, day);
        var nowUtc = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.True(zone.IsInvalidTime(start) || zone.IsAmbiguousTime(start));
        Assert.Equal(expectedSeconds, TrafficReport.SecondsForRange(start, 1, zone, nowUtc));
    }

    [Theory]
    [InlineData(2026, 3, 8, 23 * 60 * 60)]
    [InlineData(2026, 11, 1, 25 * 60 * 60)]
    public void DayLengthAccountsForDaylightSavingChanges(int year, int month, int day, double expectedSeconds)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var start = new DateTime(year, month, day);
        var now = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(start.AddDays(2), zone));

        var seconds = TrafficReport.SecondsForRange(start, 1, zone, now);

        Assert.Equal(expectedSeconds, seconds);
    }

    [Theory]
    [InlineData("=")]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("@")]
    [InlineData("\t")]
    [InlineData("\r")]
    [InlineData("\n")]
    public void CsvExportNeutralizesSpreadsheetFormulasInEveryTextColumn(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "report.csv");
        try
        {
            var row = new TrafficReportRow($"{prefix}period", $"{prefix}id", $"{prefix}name", 100, 50, 1, 2);

            TrafficReportExporter.WriteCsv(path, [row]);

            var csv = File.ReadAllText(path);
            Assert.Contains($"\"'{prefix}period\"", csv, StringComparison.Ordinal);
            Assert.Contains($"\"'{prefix}id\"", csv, StringComparison.Ordinal);
            Assert.Contains($"\"'{prefix}name\"", csv, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static TrafficBucket Bucket(
        string interfaceId,
        string interfaceName,
        DateTimeOffset minute,
        long received,
        long sent)
    {
        return new TrafficBucket(interfaceId, interfaceName, minute.ToUniversalTime(), received, sent);
    }
}
