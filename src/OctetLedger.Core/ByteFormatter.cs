using System.Globalization;

namespace OctetLedger.Core;

public static class ByteFormatter
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    public static string Format(double bytes)
    {
        var value = Math.Max(0, bytes);
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < Units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        var format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {Units[unitIndex]}";
    }

    public static string FormatRate(double bytesPerSecond) => $"{Format(bytesPerSecond)}/s";
}
