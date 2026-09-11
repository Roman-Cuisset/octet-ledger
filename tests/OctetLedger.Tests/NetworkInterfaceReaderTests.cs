using OctetLedger.Core;

namespace OctetLedger.Tests;

public class NetworkInterfaceReaderTests
{
    [Fact]
    public void CollapseDuplicatesPrefersBaseAdapterOverFilterBinding()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var snapshots = new[]
        {
            Snapshot("filter", "Wi-Fi-Npcap Packet Driver", capturedAt, 10_020),
            Snapshot("base", "Wi-Fi", capturedAt, 10_000),
            Snapshot("filter-2", "Wi-Fi-Kaspersky Lab NDIS", capturedAt, 10_010)
        };

        var result = NetworkInterfaceReader.CollapseDuplicates(snapshots);

        var selected = Assert.Single(result);
        Assert.Equal("Wi-Fi", selected.Name);
    }

    private static NetworkInterfaceSnapshot Snapshot(
        string id,
        string name,
        DateTimeOffset capturedAt,
        long received)
    {
        return new NetworkInterfaceSnapshot(
            id,
            name,
            name,
            "Wireless80211",
            "Up",
            866_000_000,
            received,
            2_000,
            capturedAt);
    }
}
