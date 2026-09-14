namespace OctetLedger.Cli;

internal static class CommandLineArguments
{
    public static bool HasFlag(string[] arguments, string flag) =>
        arguments.Contains(flag, StringComparer.OrdinalIgnoreCase);

    public static string? ReadOption(string[] arguments, string option)
    {
        var index = Array.FindIndex(arguments, value => string.Equals(value, option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }
    public static bool ValidateOptions(
        string[] arguments,
        IReadOnlyCollection<string> flags,
        IReadOnlyCollection<string> valueOptions) =>
        ValidateOptions(arguments, flags, valueOptions, Console.Error);


    public static bool ValidateOptions(
        string[] arguments,
        IReadOnlyCollection<string> flags,
        IReadOnlyCollection<string> valueOptions,
        TextWriter error)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (!seen.Add(argument))
            {
                error.WriteLine($"Option '{argument}' was specified more than once.");
                return false;
            }

            if (flags.Contains(argument, StringComparer.OrdinalIgnoreCase)) continue;
            if (valueOptions.Contains(argument, StringComparer.OrdinalIgnoreCase))
            {
                if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith('-'))
                {
                    error.WriteLine($"Option '{argument}' requires a value.");
                    return false;
                }
                index++;
                continue;
            }
            error.WriteLine($"Unknown option or argument '{argument}'.");
            return false;
        }
        return true;
    }

    public static int? ReadPositiveInteger(
        string[] arguments,
        string option,
        int defaultValue,
        int minimum,
        int maximum) =>
        ReadPositiveInteger(arguments, option, defaultValue, minimum, maximum, Console.Error);

    public static int? ReadPositiveInteger(
        string[] arguments,
        string option,
        int defaultValue,
        int minimum,
        int maximum,
        TextWriter error)
    {
        var text = ReadOption(arguments, option);
        if (text is null) return defaultValue;
        if (int.TryParse(text, out var value) && value >= minimum && value <= maximum) return value;
        error.WriteLine($"{option} must be between {minimum} and {maximum}.");
        return null;
    }
}
