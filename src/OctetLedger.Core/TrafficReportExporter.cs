using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OctetLedger.Core;

public static class TrafficReportExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string ToJson(IEnumerable<TrafficReportRow> rows)
    {
        var values = rows.Select(row => new
        {
            period = row.Period,
            interfaceId = row.InterfaceId,
            interfaceName = row.InterfaceName,
            bytesReceived = row.BytesReceived,
            bytesSent = row.BytesSent,
            totalBytes = row.TotalBytes,
            averageBytesPerSecond = Math.Round(row.AverageBytesPerSecond, 2),
            peakBytesPerSecond = Math.Round(row.PeakBytesPerSecond, 2)
        });

        return JsonSerializer.Serialize(values, JsonOptions);
    }

    public static void WriteCsv(string path, IEnumerable<TrafficReportRow> rows)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new StreamWriter(
            fullPath,
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine(
            "Period,InterfaceId,InterfaceName,BytesReceived,BytesSent,TotalBytes," +
            "AverageBytesPerSecond,PeakBytesPerSecond");

        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",",
                EscapeCsv(row.Period),
                EscapeCsv(row.InterfaceId),
                EscapeCsv(row.InterfaceName),
                row.BytesReceived.ToString(CultureInfo.InvariantCulture),
                row.BytesSent.ToString(CultureInfo.InvariantCulture),
                row.TotalBytes.ToString(CultureInfo.InvariantCulture),
                row.AverageBytesPerSecond.ToString("0.00", CultureInfo.InvariantCulture),
                row.PeakBytesPerSecond.ToString("0.00", CultureInfo.InvariantCulture)));
        }
    }

    private static string EscapeCsv(string value)
    {
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
