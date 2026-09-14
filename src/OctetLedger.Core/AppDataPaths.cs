namespace OctetLedger.Core;

public static class AppDataPaths
{
    private static string? configuredDataDirectory;

    public static string DataDirectory => configuredDataDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OctetLedger");

    public static string DatabasePath => Path.Combine(DataDirectory, "octetledger.db");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static bool IsPortable => configuredDataDirectory is not null;

    public static void ConfigureDataDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Data directory cannot be empty.", nameof(path));
        configuredDataDirectory = Path.GetFullPath(path);
    }
}
