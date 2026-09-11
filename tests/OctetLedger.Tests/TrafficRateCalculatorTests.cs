using OctetLedger.Core;

namespace OctetLedger.Tests;

public class TrafficRateCalculatorTests
{
    [Fact]
    public void CalculateReturnsRatesFromCounterDeltas()
    {
        var previous = Snapshot(received: 1_000, sent: 2_000, second: 0);
        var current = Snapshot(received: 3_000, sent: 3_000, second: 2);

        var rate = TrafficRateCalculator.Calculate(previous, current);

        Assert.Equal(1_000, rate.ReceivedBytesPerSecond);
        Assert.Equal(500, rate.SentBytesPerSecond);
        Assert.Equal(1_500, rate.TotalBytesPerSecond);
    }

    [Fact]
    public void CalculateDoesNotReportNegativeRatesAfterCounterReset()
    {
        var previous = Snapshot(received: 5_000, sent: 6_000, second: 0);
        var current = Snapshot(received: 100, sent: 200, second: 1);

        var rate = TrafficRateCalculator.Calculate(previous, current);

        Assert.Equal(0, rate.ReceivedBytesPerSecond);
        Assert.Equal(0, rate.SentBytesPerSecond);
    }

    private static NetworkInterfaceSnapshot Snapshot(long received, long sent, int second)
    {
        return new NetworkInterfaceSnapshot(
            "interface-id",
            "Wi-Fi",
            "Test adapter",
            "Wireless80211",
            "Up",
            1_000_000_000,
            received,
            sent,
            new DateTimeOffset(2026, 1, 1, 0, 0, second, TimeSpan.Zero));
    }
}
