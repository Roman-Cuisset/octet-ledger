namespace OctetLedger.Core;

public static class AppDataPaths
{
    private static string? configuredDataDirectory;

    public static string DataDirectory => configuredDataDirectory ?? Path.Combine(
        DefaultLocalApplicationDataDirectory,
        "OctetLedger");

    private static string DefaultLocalApplicationDataDirectory
    {
        get
        {
            var configured = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("LOCALAPPDATA") : null;
            return !string.IsNullOrWhiteSpace(configured)
                ? configured
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
    }

    public static string DatabasePath => Path.Combine(DataDirectory, "octetledger.db");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static bool IsPortable => configuredDataDirectory is not null;

    public static void ConfigureDataDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Data directory cannot be empty.", nameof(path));
        configuredDataDirectory = Path.GetFullPath(path);
    }
}
