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

    [Theory]
    [InlineData("Wi-Fi", "Intel(R) Wireless-AC 9560 160MHz", "Wireless80211", true)]
    [InlineData("Ethernet", "Realtek PCIe GbE Family Controller", "Ethernet", true)]
    [InlineData("Connexion au réseau local* 8", "WAN Miniport (IP)", "Ethernet", false)]
    [InlineData("Connexion au réseau local* 11", "WAN Miniport (IPv6)", "Ethernet", false)]
    [InlineData("Connexion au réseau local* 12", "WAN Miniport (Network Monitor)", "Ethernet", false)]
    [InlineData("Connexion au réseau local* 1", "Microsoft Wi-Fi Direct Virtual Adapter #3", "Wireless80211", false)]
    [InlineData("Tailscale", "Tailscale Tunnel", "53", false)]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", "Ethernet", false)]
    [InlineData("Loopback", "Software Loopback Interface 1", "Loopback", false)]
    public void IsLikelyPhysicalIdentifiesRealHardwareAndExcludesSynthesizedAdapters(
        string name, string description, string type, bool expected)
    {
        Assert.Equal(expected, NetworkInterfaceSelector.IsLikelyPhysical(name, description, type));
    }

    private static NetworkInterfaceSnapshot Snapshot(string id, string name, string type, long received)
    {
        return new NetworkInterfaceSnapshot(
            id, name, name, type, "Up", 1_000_000_000, received, 1_000, DateTimeOffset.UtcNow);
    }
}
