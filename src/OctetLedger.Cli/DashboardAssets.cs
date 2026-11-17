namespace OctetLedger.Cli;

internal static class DashboardAssets
{
    internal static readonly IReadOnlyDictionary<string, (byte[] Bytes, string ContentType)> Files =
        new Dictionary<string, (byte[], string)>(StringComparer.Ordinal)
        {
            ["/"] = (Read("index.html"), "text/html; charset=utf-8"),
            ["/dashboard.css"] = (Read("dashboard.css"), "text/css; charset=utf-8"),
            ["/dashboard.js"] = (Read("dashboard.js"), "text/javascript; charset=utf-8")
        };

    private static byte[] Read(string filename)
    {
        using var resource = typeof(DashboardAssets).Assembly.GetManifestResourceStream($"OctetLedger.Dashboard.{filename}")
            ?? throw new InvalidOperationException($"Missing embedded dashboard resource: {filename}");
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        return bytes.ToArray();
    }
}
