namespace OctetLedger.Core;

public static class NetworkInterfaceSelector
{
    public static NetworkInterfaceSnapshot? SelectPrimary(
        IEnumerable<NetworkInterfaceSnapshot> snapshots,
        string? preferredInterfaceId = null)
    {
        var active = snapshots
            .Where(snapshot =>
                snapshot.Status == "Up" &&
                snapshot.Type is not ("Loopback" or "Tunnel") &&
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
            .OrderByDescending(snapshot => snapshot.Type is "Wireless80211" or "Ethernet")
            .ThenBy(snapshot => IsVirtual(snapshot.Name))
            .ThenByDescending(snapshot => snapshot.BytesReceived + snapshot.BytesSent)
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

    private static bool IsVirtual(string name)
    {
        return name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("vSwitch", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("VPN", StringComparison.OrdinalIgnoreCase);
    }
}
