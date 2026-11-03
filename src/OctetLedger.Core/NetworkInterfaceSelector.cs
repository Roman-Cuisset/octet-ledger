namespace OctetLedger.Core;

public static class NetworkInterfaceSelector
{
    private static readonly string[] VirtualMarkers =
    [
        "Async Adapter",
        "Bluetooth",
        "Hyper-V",
        "IP-HTTPS",
        "Kernel Debug",
        "Loopback",
        "ISATAP",
        "Npcap",
        "Pseudo-Interface",
        "Tailscale",
        "OpenVPN",
        "TAP",
        "Teredo",
        "Tunnel",
        "Virtual",
        "VirtualBox",
        "VMware",
        "VPN",
        "vEthernet",
        "vSwitch",
        "WAN Miniport",
        "Wi-Fi Direct",
        "WireGuard",
        "Wintun",
        "WSL"
    ];

    public static NetworkInterfaceSnapshot? SelectPrimary(
        IEnumerable<NetworkInterfaceSnapshot> snapshots,
        string? preferredInterfaceId = null)
    {
        var active = snapshots.Where(snapshot => snapshot.Status == "Up").ToArray();

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
            .Where(snapshot => IsLikelyPhysical(snapshot.Name, snapshot.Description, snapshot.Type))
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

        // The friendly name can be freely renamed. Prefer the device/driver description;
        // use the name only when older stored metadata has no description.
        var identity = string.IsNullOrWhiteSpace(description) ? name : description;
        foreach (var marker in VirtualMarkers)
            if (ContainsMarker(identity, marker)) return false;
        return true;
    }

    private static bool ContainsMarker(string identity, string marker)
    {
        var start = 0;
        while (start < identity.Length)
        {
            var index = identity.IndexOf(marker, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            var end = index + marker.Length;
            if ((index == 0 || !char.IsLetterOrDigit(identity[index - 1])) &&
                (end == identity.Length || !char.IsLetterOrDigit(identity[end])))
                return true;
            start = end;
        }
        return false;
    }
}
