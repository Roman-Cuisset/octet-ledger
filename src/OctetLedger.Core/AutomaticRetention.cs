namespace OctetLedger.Core;

/// <summary>Checks configured raw-data retention on startup and once per local calendar day.</summary>
public sealed class AutomaticRetention
{
    private readonly string settingsPath;
    private readonly TimeZoneInfo timeZone;
    private DateTime? lastSuccessfulDay;

    public AutomaticRetention() : this(OctetLedgerSettings.SettingsPath, TimeZoneInfo.Local) { }

    internal AutomaticRetention(string settingsPath, TimeZoneInfo timeZone)
    {
        this.settingsPath = settingsPath;
        this.timeZone = timeZone;
    }

    public RetentionResult? RunIfDue(TrafficStore store, DateTimeOffset nowUtc)
    {
        var localDay = TimeZoneInfo.ConvertTime(nowUtc, timeZone).Date;
        if (lastSuccessfulDay == localDay) return null;

        var settings = OctetLedgerSettings.Load(settingsPath);
        var result = store.ApplyRetention(settings.RetentionRawDays, nowUtc, timeZone, onlyIfDue: true);
        // Only cache success: an interrupted transaction must be retried, even on the same day.
        lastSuccessfulDay = localDay;
        return result;
    }
}
