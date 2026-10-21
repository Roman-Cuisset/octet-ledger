// A real executable that passes version preflight but cannot read the installed schema.
if (args.Length == 1 && args[0] == "version")
{
    Console.WriteLine("OctetLedger 0.0.0-validation");
    return 0;
}

if (args.Length == 3 && args[0] == "database" && args[1] == "check")
{
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "rollback-validation-database-check"),
        DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    Console.Error.WriteLine("Validation candidate cannot read the installed database schema.");
    return 1;
}

Console.Error.WriteLine("This executable is only a rollback schema-incompatibility fixture.");
return 2;
