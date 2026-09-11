using System.Net.NetworkInformation;

namespace OctetLedger.Core;

public static class NetworkInterfaceReader
{
    public static IReadOnlyList<NetworkInterfaceSnapshot> ReadAll()
    {
        var capturedAt = DateTimeOffset.Now;
        var snapshots = new List<NetworkInterfaceSnapshot>();

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var statistics = networkInterface.GetIPStatistics();
                snapshots.Add(new NetworkInterfaceSnapshot(
                    networkInterface.Id,
                    networkInterface.Name,
                    networkInterface.Description,
                    networkInterface.NetworkInterfaceType.ToString(),
                    networkInterface.OperationalStatus.ToString(),
                    networkInterface.Speed,
                    statistics.BytesReceived,
                    statistics.BytesSent,
                    capturedAt));
            }
            catch (NetworkInformationException)
            {
                // Some short-lived virtual adapters disappear while being read.
            }
        }

        return snapshots
            .OrderByDescending(snapshot => snapshot.Status == OperationalStatus.Up.ToString())
            .ThenBy(snapshot => snapshot.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<NetworkInterfaceSnapshot> ReadDistinct()
    {
        return CollapseDuplicates(ReadAll());
    }

    public static IReadOnlyList<NetworkInterfaceSnapshot> CollapseDuplicates(
        IEnumerable<NetworkInterfaceSnapshot> snapshots)
    {
        var snapshotList = snapshots.ToArray();
        var visibleSnapshots = snapshotList.Where(snapshot =>
        {
            var baseName = GetBaseAdapterName(snapshot.Name);
            return baseName is null || !snapshotList.Any(candidate =>
                string.Equals(candidate.Name, baseName, StringComparison.OrdinalIgnoreCase));
        });
        var result = new List<NetworkInterfaceSnapshot>();

        foreach (var group in visibleSnapshots.GroupBy(snapshot => new
                 {
                     snapshot.Status,
                     snapshot.Type,
                     snapshot.BytesReceived,
                     snapshot.BytesSent
                 }))
        {
            if (group.Key.Status == OperationalStatus.Up.ToString())
            {
                result.Add(group
                    .OrderBy(AdapterNamePenalty)
                    .ThenBy(snapshot => snapshot.Name.Length)
                    .First());
            }
            else
            {
                result.AddRange(group);
            }
        }

        return result
            .OrderByDescending(snapshot => snapshot.Status == OperationalStatus.Up.ToString())
            .ThenBy(snapshot => snapshot.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static NetworkInterfaceSnapshot? Find(string selector)
    {
        return ReadAll().FirstOrDefault(snapshot =>
            string.Equals(snapshot.Id, selector, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(snapshot.Name, selector, StringComparison.OrdinalIgnoreCase));
    }

    private static int AdapterNamePenalty(NetworkInterfaceSnapshot snapshot)
    {
        return GetBaseAdapterName(snapshot.Name) is null ? 0 : 1;
    }

    private static string? GetBaseAdapterName(string name)
    {
        string[] filterSuffixes =
        [
            "-Kaspersky",
            "-Npcap",
            "-QoS",
            "-WFP",
            "-Native",
            "-VirtualBox",
            "-Virtual WiFi",
            "-Hyper-V"
        ];

        var suffixIndex = filterSuffixes
            .Select(suffix => name.IndexOf(suffix, StringComparison.OrdinalIgnoreCase))
            .Where(index => index > 0)
            .DefaultIfEmpty(-1)
            .Min();

        return suffixIndex > 0 ? name[..suffixIndex] : null;
    }
}
