using OctetLedger.Core;

namespace OctetLedger.Tests;

public class NetworkInterfaceClassificationTests
{
    [Theory]
    [InlineData("VPN uplink", "Intel(R) Ethernet Connection I219-V", "Ethernet", true)]
    [InlineData("WAN * studio", "Realtek PCIe GbE Family Controller", "Ethernet", true)]
    [InlineData("TAP capture", "Intel(R) Wi-Fi 6 AX200", "Wireless80211", true)]
    [InlineData("Virtual office", "Intel(R) Wi-Fi 6 AX200", "Wireless80211", true)]
    [InlineData("Ethernet", "Taiwan USB Gigabit Ethernet Adapter", "Ethernet", true)]
    [InlineData("Ethernet", "Ethernet Miniport Adapter", "Ethernet", true)]
    [InlineData("Ethernet", "Virtualink USB Ethernet Adapter", "Ethernet", true)]
    [InlineData("Renamed Wi-Fi", "Microsoft Wi-Fi Direct Virtual Adapter #2", "Wireless80211", false)]
    [InlineData("Ethernet", "WAN Miniport (IP)", "Ethernet", false)]
    [InlineData("Ethernet", "TAP-Windows Adapter V9", "Ethernet", false)]
    [InlineData("Ethernet", "OpenVPN Data Channel Offload", "Ethernet", false)]
    [InlineData("Ethernet", "Microsoft ISATAP Adapter", "Ethernet", false)]
    [InlineData("Ethernet", "Microsoft Kernel Debug Network Adapter", "Ethernet", false)]
    [InlineData("Ethernet", "VMware Virtual Ethernet Adapter", "Ethernet", false)]
    [InlineData("Ethernet", "WireGuard Tunnel", "Ethernet", false)]
    [InlineData("Ethernet", "Wintun Userspace Tunnel", "Ethernet", false)]
    [InlineData("TAP-Windows Adapter V9", "", "Ethernet", false)]
    [InlineData("Ethernet USB", "", "Ethernet", true)]
    [InlineData("VPN uplink", "Intel(R) Ethernet Connection I219-V", "Tunnel", false)]
    public void DriverDescriptionAndDelimitedMarkersAvoidFriendlyNameFalsePositives(string name, string description, string type, bool expected)
    {
        Assert.Equal(expected, NetworkInterfaceSelector.IsLikelyPhysical(name, description, type));
    }

    [Fact]
    public void RenamingHardwareDoesNotLetVirtualTrafficReplaceThePrimaryInterface()
    {
        var snapshots = new[]
        {
            Snapshot("ethernet", "WAN * VPN uplink", "Intel Ethernet", "Ethernet", 200),
            Snapshot("wifi", "TAP office", "Intel Wi-Fi", "Wireless80211", 100),
            Snapshot("vpn", "Ethernet", "TAP-Windows Adapter V9", "Ethernet", 10_000)
        };

        Assert.Equal("ethernet", NetworkInterfaceSelector.SelectPrimary(snapshots)?.Id);
        Assert.Equal("vpn", NetworkInterfaceSelector.SelectPrimary(snapshots, "vpn")?.Id);
        Assert.Equal("wifi", NetworkInterfaceSelector.SelectPrimary(snapshots, "wifi")?.Id);
    }

    [Theory]
    [InlineData(null, "ethernet,wifi", 450)]
    [InlineData("vpn", "vpn", 10_000)]
    [InlineData("ethernet", "ethernet", 300)]
    public void HistoricalReportsPreserveHardwareChangesAndHonorSavedPreference(string? preference, string expectedIds, long expectedBytes)
    {
        var buckets = Buckets();
        var selected = TrafficReport.SelectInterface(buckets, null, preference);
        var selectedRows = TrafficReport.SelectInterfaceRows(TrafficReport.Total(buckets), null, preference);

        Assert.Equal(expectedIds, string.Join(',', selected.Select(bucket => bucket.InterfaceId).Order(StringComparer.Ordinal)));
        Assert.Equal(expectedIds, string.Join(',', selectedRows.Select(row => row.InterfaceId).Order(StringComparer.Ordinal)));
        Assert.Equal(expectedBytes, selected.Sum(bucket => bucket.BytesReceived + bucket.BytesSent));
        Assert.Equal(expectedBytes, selectedRows.Sum(row => row.TotalBytes));
    }

    [Fact]
    public void ExplicitVirtualSelectorOverridesPhysicalPreferenceInHistoricalReports()
    {
        var buckets = Buckets();
        var selected = Assert.Single(TrafficReport.SelectInterface(buckets, "vpn", "ethernet"));
        var row = Assert.Single(TrafficReport.SelectInterfaceRows(TrafficReport.Total(buckets), "vpn", "ethernet"));

        Assert.Equal("vpn", selected.InterfaceId);
        Assert.Equal(10_000, selected.BytesReceived + selected.BytesSent);
        Assert.Equal("vpn", row.InterfaceId);
        Assert.Equal(10_000, row.TotalBytes);
    }

    private static TrafficBucket[] Buckets() =>
    [
        new("ethernet", "WAN * VPN uplink", DateTimeOffset.UnixEpoch, 200, 100,
            InterfaceDescription: "Intel Ethernet", InterfaceType: "Ethernet"),
        new("wifi", "TAP office", DateTimeOffset.UnixEpoch, 100, 50,
            InterfaceDescription: "Intel Wi-Fi", InterfaceType: "Wireless80211"),
        new("vpn", "Ethernet", DateTimeOffset.UnixEpoch, 5_000, 5_000,
            InterfaceDescription: "TAP-Windows Adapter V9", InterfaceType: "Ethernet")
    ];

    private static NetworkInterfaceSnapshot Snapshot(string id, string name, string description, string type, long received) =>
        new(id, name, description, type, "Up", 1_000_000_000, received, 0, DateTimeOffset.UnixEpoch);
}
