using OctetLedger.Cli;

namespace OctetLedger.Tests;

public class BudgetCommandTests
{
    [Theory]
    [InlineData("1.5TB", 1_500_000_000_000L)]
    [InlineData("1GiB", 1_073_741_824L)]
    [InlineData("0.6B", 1L)]
    [InlineData("9223372036854775807B", long.MaxValue)]
    public void PositiveBudgetSizesAreConvertedWithoutOverflow(string text, long expected)
    {
        Assert.True(BudgetCommand.TryParseBytes(text, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("0.01B")]
    [InlineData("0.5B")]
    [InlineData("0GB")]
    [InlineData("9223372036854775808B")]
    [InlineData("79228162514264337593543950335TB")]
    public void BudgetSizesOutsidePositiveLongRangeAreRejected(string text)
    {
        Assert.False(BudgetCommand.TryParseBytes(text, out _));
    }
}
