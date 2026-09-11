namespace OctetLedger.Core;

public static class AppDataPaths
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OctetLedger");

    public static string DatabasePath => Path.Combine(DataDirectory, "octetledger.db");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
}
