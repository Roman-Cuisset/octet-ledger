using OctetLedger.Core;

namespace OctetLedger.Tests;

public class ByteFormatterTests
{
    [Theory]
    [InlineData(0, "0.00 B")]
    [InlineData(1024, "1.00 KiB")]
    [InlineData(1_048_576, "1.00 MiB")]
    [InlineData(1_073_741_824, "1.00 GiB")]
    [InlineData(-1, "0.00 B")]
    public void FormatUsesBinaryUnits(double bytes, string expected)
    {
        Assert.Equal(expected, ByteFormatter.Format(bytes));
    }
}
