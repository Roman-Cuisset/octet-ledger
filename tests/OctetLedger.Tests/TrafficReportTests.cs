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

    [Fact]
    public void JsonExportContainsNumericByteValues()
    {
        var row = new TrafficReportRow("2026-09-11", "wifi", "Wi-Fi", 100, 50, 1, 2);

        var json = TrafficReportExporter.ToJson([row]);

        Assert.Contains("\"totalBytes\": 150", json, StringComparison.Ordinal);
        Assert.Contains("\"interfaceName\": \"Wi-Fi\"", json, StringComparison.Ordinal);
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
