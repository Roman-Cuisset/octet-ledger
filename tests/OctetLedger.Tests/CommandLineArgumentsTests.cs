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
    }

    [Fact]
    public void MissingOptionValueIsRejected()
    {
        using var error = new StringWriter();

        var valid = CommandLineArguments.ValidateOptions(["--csv"], [], ["--csv"], error);

        Assert.False(valid);
    }

    [Fact]
    public void BoundedIntegerRejectsOutOfRangeValue()
    {
        using var error = new StringWriter();

        var value = CommandLineArguments.ReadPositiveInteger(["--days", "0"], "--days", 30, 1, 3660, error);

        Assert.Null(value);
    }
}
