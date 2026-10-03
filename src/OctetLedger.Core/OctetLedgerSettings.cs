using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OctetLedger.Core;

public sealed record OctetLedgerSettings(
    string? PreferredInterfaceId = null,
    bool CheckForUpdates = true,
    DateTimeOffset? LastUpdateCheckUtc = null,
    DateTimeOffset? LastSuccessfulUpdateCheckUtc = null,
    string? LatestKnownVersion = null,
    long? MonthlyBudgetBytes = null,
    int RetentionRawDays = 90,
    bool AutomaticBackups = false,
    DateTimeOffset? LastAutomaticBackupUtc = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsPath => AppDataPaths.SettingsPath;

    public static OctetLedgerSettings Load() => Load(SettingsPath);

    internal static OctetLedgerSettings Load(string settingsPath)
    {
        if (!File.Exists(settingsPath))
        {
            return new OctetLedgerSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<OctetLedgerSettings>(File.ReadAllText(settingsPath), JsonOptions)
                   ?? throw new InvalidDataException($"Settings file '{settingsPath}' does not contain settings.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Settings file '{settingsPath}' contains invalid JSON.", exception);
        }
    }

    public static OctetLedgerSettings Update(Func<OctetLedgerSettings, OctetLedgerSettings> update) => Update(SettingsPath, update);

    internal static OctetLedgerSettings Update(string settingsPath, Func<OctetLedgerSettings, OctetLedgerSettings> update)
    {
        var path = Path.GetFullPath(settingsPath);
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        var lockId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
        using var mutex = new Mutex(false, $"OctetLedger.Settings.{lockId}");
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { }
        try
        {
            var settings = update(Load(settingsPath));
            settings.Save(settingsPath);
            return settings;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private void Save(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = $"{settingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temporaryPath, settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
