namespace OctetLedger.Core;

public static class NetworkInterfaceSelector
{
    private static readonly string[] VirtualMarkers =
    [
        "Bluetooth",
        "Hyper-V",
        "IP-HTTPS",
        "Npcap",
        "Tailscale",
        "Teredo",
        "Tunnel",
        "Virtual",
        "VirtualBox",
        "VMware",
        "VPN",
        "vEthernet",
        "vSwitch",
        "WSL"
    ];

    public static NetworkInterfaceSnapshot? SelectPrimary(
        IEnumerable<NetworkInterfaceSnapshot> snapshots,
        string? preferredInterfaceId = null)
    {
        var active = snapshots
            .Where(snapshot =>
                snapshot.Status == "Up" &&
                IsLikelyPhysical(snapshot.Name, snapshot.Description, snapshot.Type) &&
                snapshot.BytesReceived + snapshot.BytesSent > 0)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(preferredInterfaceId))
        {
            var preferred = active.FirstOrDefault(snapshot =>
                string.Equals(snapshot.Id, preferredInterfaceId, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return active
            .OrderByDescending(snapshot => snapshot.BytesReceived + snapshot.BytesSent)
            .FirstOrDefault();
    }

    public static NetworkInterfaceSnapshot? Resolve(
        IEnumerable<NetworkInterfaceSnapshot> snapshots,
        string selector)
    {
        return snapshots.FirstOrDefault(snapshot =>
            string.Equals(snapshot.Id, selector, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(snapshot.Name, selector, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsLikelyPhysical(string name, string description, string type)
    {
        if (type != "Wireless80211" && !type.Contains("Ethernet", StringComparison.OrdinalIgnoreCase))
            return false;
        return !VirtualMarkers.Any(marker =>
            name.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
            description.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
