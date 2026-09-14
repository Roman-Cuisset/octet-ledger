namespace OctetLedger.Core;

public static class AutomaticBackup
{
    public static string BackupDirectory => Path.Combine(AppDataPaths.DataDirectory, "backups");

    public static string? RunIfDue(DateTimeOffset nowUtc)
    {
        var settings = OctetLedgerSettings.Load();
        if (!settings.AutomaticBackups || settings.LastAutomaticBackupUtc is { } last && nowUtc - last < TimeSpan.FromHours(24)) return null;
        Directory.CreateDirectory(BackupDirectory);
        var path = Path.Combine(BackupDirectory, $"octetledger-{nowUtc:yyyyMMdd-HHmmss}.db");
        using (var store = new TrafficStore()) store.Backup(path);
        foreach (var obsolete in Directory.GetFiles(BackupDirectory, "octetledger-*.db").OrderByDescending(File.GetCreationTimeUtc).Skip(7))
            File.Delete(obsolete);
        (settings with { LastAutomaticBackupUtc = nowUtc }).Save();
        return path;
    }
}
