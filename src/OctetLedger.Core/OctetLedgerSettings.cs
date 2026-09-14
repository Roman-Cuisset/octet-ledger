using System.Text.Json;

namespace OctetLedger.Core;

public sealed record OctetLedgerSettings(
    string? PreferredInterfaceId = null,
    bool CheckForUpdates = true,
    DateTimeOffset? LastUpdateCheckUtc = null,
    string? LatestKnownVersion = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsPath => AppDataPaths.SettingsPath;

    public static OctetLedgerSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new OctetLedgerSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<OctetLedgerSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                   ?? new OctetLedgerSettings();
        }
        catch (JsonException)
        {
            return new OctetLedgerSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDataPaths.DataDirectory);
        var temporaryPath = $"{SettingsPath}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }
}
