using OctetLedger.Core;

// Real process/SQLite fixtures used only by isolated installation validation.
var mode = Environment.GetEnvironmentVariable("OCTETLEDGER_VALIDATION_MODE");
var version = args.Length == 1 && args[0] == "version";
var installing = args.Length == 2 && args[0] == "collector" && args[1] == "install";
var checking = args.Length == 3 && args[0] == "database" && args[1] == "check";
if (mode == "echo")
{
    Console.Write(string.Join("\n", args));
    return 0;
}
if (mode == "hang-version" && version || mode == "hang-install" && installing ||
    mode == "hang-check" && checking)
{
    var marker = Environment.GetEnvironmentVariable("OCTETLEDGER_VALIDATION_PID_FILE");
    if (marker is not null) File.WriteAllText(marker, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Thread.Sleep(Timeout.Infinite);
}
if (mode == "fail-install" && installing)
{
    var marker = Environment.GetEnvironmentVariable("OCTETLEDGER_VALIDATION_PID_FILE");
    if (marker is not null)
    {
        var profile = Path.Combine(Path.GetDirectoryName(marker)!, "localappdata");
        if (string.Equals(Environment.GetEnvironmentVariable("LOCALAPPDATA"), profile, StringComparison.OrdinalIgnoreCase))
            File.WriteAllText(Path.Combine(profile, "OctetLedger", "collector.vbs"), "' Incomplete candidate launcher\r\n");
    }
    Console.Error.WriteLine("Validation fixture rejected collector installation.");
    return 42;
}
if (args.Length == 3 && args[0] == "database" && args[1] is "seed" or "verify")
{
    const string name = "octetledger-lifecycle-preserved.exe";
    if (args[1] == "seed")
        ApplicationTrafficStore.Add(DateTimeOffset.UtcNow, [new ApplicationTrafficRow(name, 4_096, 1_024)], args[2]);
    var row = ApplicationTrafficStore.ReadTop(DateTimeOffset.UnixEpoch, 100, args[2]).Single(value => value.ProcessName == name);
    if (row.BytesReceived != 4_096 || row.BytesSent != 1_024) throw new InvalidDataException("Installed data was changed.");
    Console.WriteLine("Preserved lifecycle application bytes: 5120.");
    return 0;
}
if (version)
{
    Console.WriteLine("OctetLedger 0.0.0-validation");
    return 0;
}
if (checking)
{
    if (mode is not null) return 0;
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "rollback-validation-database-check"),
        DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    Console.Error.WriteLine("Validation candidate cannot read the installed database schema.");
    return 1;
}
if (mode is not null && args.Length == 2 && args[0] == "collector") return 0;
Console.Error.WriteLine("This executable is only an isolated installation validation fixture.");
return 2;
