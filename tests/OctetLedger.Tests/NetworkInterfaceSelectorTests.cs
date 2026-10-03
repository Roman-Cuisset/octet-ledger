using OctetLedger.Core;

namespace OctetLedger.Tests;

public class NetworkInterfaceSelectorTests
{
    [Fact]
    public void SelectPrimaryPrefersPhysicalAdapterOverVpn()
    {
        var snapshots = new[]
        {
            Snapshot("vpn", "Tailscale", "53", 10_000),
            Snapshot("wifi", "Wi-Fi", "Wireless80211", 5_000)
        };

        var selected = NetworkInterfaceSelector.SelectPrimary(snapshots);

        Assert.NotNull(selected);
        Assert.Equal("wifi", selected.Id);
    }

    [Fact]
    public void SelectPrimaryHonorsAvailablePreference()
    {
        var snapshots = new[]
        {
            Snapshot("ethernet", "Ethernet", "Ethernet", 10_000),
            Snapshot("wifi", "Wi-Fi", "Wireless80211", 5_000)
        };

        var selected = NetworkInterfaceSelector.SelectPrimary(snapshots, "wifi");

        Assert.NotNull(selected);
        Assert.Equal("wifi", selected.Id);
    }

    [Fact]
    public void SelectPrimaryHonorsExplicitVirtualInterfacePreference()
    {
        var snapshots = new[]
        {
            Snapshot("ethernet", "Ethernet", "Ethernet", 10_000),
            Snapshot("vpn", "Tailscale", "53", 5_000)
        };

        Assert.Equal("vpn", NetworkInterfaceSelector.SelectPrimary(snapshots, "vpn")?.Id);
    }

    [Fact]
    public void SelectPrimaryIncludesAnActiveAdapterBeforeItsFirstByte()
    {
        var snapshot = Snapshot("wifi", "Wi-Fi", "Wireless80211", 0) with { BytesSent = 0 };

        Assert.Equal("wifi", NetworkInterfaceSelector.SelectPrimary([snapshot])?.Id);
    }

    private static NetworkInterfaceSnapshot Snapshot(string id, string name, string type, long received)
    {
        return new NetworkInterfaceSnapshot(
            id, name, name, type, "Up", 1_000_000_000, received, 1_000, DateTimeOffset.UtcNow);
    }
}
