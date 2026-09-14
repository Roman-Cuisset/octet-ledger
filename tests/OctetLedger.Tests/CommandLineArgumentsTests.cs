using OctetLedger.Cli;

namespace OctetLedger.Tests;

public class CommandLineArgumentsTests
{
    [Fact]
    public void DuplicateOptionIsRejected()
    {
        using var error = new StringWriter();

        var valid = CommandLineArguments.ValidateOptions(
            ["--days", "7", "--days", "30"],
            [],
            ["--days"],
            error);

        Assert.False(valid);
        Assert.Contains("specified more than once", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOptionValueIsRejected()
    {
        using var error = new StringWriter();

        var valid = CommandLineArguments.ValidateOptions(["--csv"], [], ["--csv"], error);

        Assert.False(valid);
        Assert.Contains("requires a value", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedIntegerReportsOutOfRangeValue()
    {
        using var error = new StringWriter();

        var value = CommandLineArguments.ReadPositiveInteger(["--days", "0"], "--days", 30, 1, 3660, error);

        Assert.Null(value);
        Assert.Contains("between 1 and 3660", error.ToString(), StringComparison.Ordinal);
    }
}
