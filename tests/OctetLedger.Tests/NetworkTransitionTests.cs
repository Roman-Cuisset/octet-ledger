using OctetLedger.Core;

namespace OctetLedger.Tests;

public class NetworkTransitionTests
{
    [Fact]
    public void PrimarySelectionFollowsPhysicalDisconnectAndReconnectWithoutFollowingVpn()
    {
        var wifi = Snapshot("wifi", "Wi-Fi", "Wireless80211", "Up", 10_000);
        var ethernet = Snapshot("ethernet", "Ethernet", "Ethernet", "Down", 20_000);
        var vpn = Snapshot("vpn", "VPN", "Tunnel", "Down", 100_000);

        Assert.Equal("wifi", NetworkInterfaceSelector.SelectPrimary([wifi, ethernet, vpn])?.Id);
        vpn = vpn with { Status = "Up" };
        Assert.Equal("wifi", NetworkInterfaceSelector.SelectPrimary([wifi, ethernet, vpn])?.Id);

        wifi = wifi with { Status = "Down" };
        ethernet = ethernet with { Status = "Up" };
        Assert.Equal("ethernet", NetworkInterfaceSelector.SelectPrimary([wifi, ethernet, vpn], "wifi")?.Id);
        Assert.Equal("vpn", NetworkInterfaceSelector.SelectPrimary([wifi, ethernet, vpn], "vpn")?.Id);

        ethernet = ethernet with { Status = "Down" };
        Assert.Null(NetworkInterfaceSelector.SelectPrimary([wifi, ethernet, vpn]));
        wifi = wifi with { Status = "Up", BytesReceived = 0, BytesSent = 0 };
        Assert.Equal("wifi", NetworkInterfaceSelector.SelectPrimary([wifi, ethernet, vpn], "wifi")?.Id);
    }

    [Fact]
    public void RateNeverSubtractsCountersAcrossPhysicalAdapterSwitch()
    {
        var wifi = Snapshot("wifi", "Wi-Fi", "Wireless80211", "Up", 10_000);
        var ethernet = Snapshot("ethernet", "Ethernet", "Ethernet", "Up", 20_000)
            with
        { CapturedAt = wifi.CapturedAt.AddSeconds(60) };

        Assert.Throws<ArgumentException>(() => TrafficRateCalculator.Calculate(wifi, ethernet));
    }

    [Theory]
    [InlineData("2026-03-08T01:59:30-05:00", "2026-03-08T03:00:30-04:00")]
    [InlineData("2026-11-01T01:59:30-04:00", "2026-11-01T01:00:30-05:00")]
    public void RateUsesElapsedInstantAcrossDstTransition(string before, string after)
    {
        var first = Snapshot("wifi", "Wi-Fi", "Wireless80211", "Up", 1_000)
            with
        { CapturedAt = DateTimeOffset.Parse(before, System.Globalization.CultureInfo.InvariantCulture), BytesSent = 500 };
        var second = first with
        {
            CapturedAt = DateTimeOffset.Parse(after, System.Globalization.CultureInfo.InvariantCulture),
            BytesReceived = 7_000,
            BytesSent = 3_500
        };

        var result = TrafficRateCalculator.Calculate(first, second);

        Assert.Equal(100, result.ReceivedBytesPerSecond);
        Assert.Equal(50, result.SentBytesPerSecond);
    }

    private static NetworkInterfaceSnapshot Snapshot(string id, string name, string type, string status, long received) =>
        new(id, name, name, type, status, 1_000_000_000, received, 0, DateTimeOffset.UnixEpoch);
}
