using Microsoft.Data.Sqlite;
using OctetLedger.Core;

namespace OctetLedger.Tests;

public class AutomaticRetentionTests
{
    [Fact]
    public void ConfiguredRetentionPreservesTotalsZeroObservationsAndRecentDetail()
    {
        using var database = new TestDatabase(2);
        using var store = new TrafficStore(database.Path);
        Seed(store);
        var now = Start.AddDays(3);
        var before = Assert.Single(store.ReadTotalReportRows(now));

        var result = database.Maintenance().RunIfDue(store, now);

        Assert.Equal(2, result?.RawMinutesArchived);
        var total = Assert.Single(store.ReadTotalReportRows(now));
        Assert.Equal(before.BytesReceived, total.BytesReceived);
        Assert.Equal(before.BytesSent, total.BytesSent);
        Assert.Equal("mixed", total.Precision);
        var buckets = store.ReadBuckets(DateTimeOffset.UnixEpoch);
        var archived = Assert.Single(buckets, bucket => bucket.IsDailyArchive);
        Assert.Equal(600, archived.BytesReceived + archived.BytesSent);
        Assert.Equal(120, archived.IntervalSeconds);
        var recent = Assert.Single(buckets, bucket => !bucket.IsDailyArchive);
        Assert.Equal(300, recent.BytesReceived + recent.BytesSent);
        var history = store.ReadCollectionHistory(Start, now, TimeZoneInfo.Utc);
        Assert.Equal(4, history.Days.Sum(day => day.Observations));
        Assert.Equal(1, history.Days.Sum(day => day.ZeroTrafficIntervals));
        Assert.Equal(Start.Date.AddDays(1), history.RawHistoryFromUtc?.UtcDateTime);
        Assert.Equal(TrafficStore.CurrentSchemaVersion, store.ReadSchemaVersion());
        Assert.True(store.CheckIntegrity().IsHealthy);
    }

    [Fact]
    public void SameDayAndRestartSkipArchivingButNextLocalDateRunsMaintenance()
    {
        using var database = new TestDatabase(1);
        var maintenance = database.Maintenance();
        var now = Start.AddDays(1);
        using (var store = new TrafficStore(database.Path))
        {
            store.Collect([Snapshot(Start, 100, 0)]);
            store.Collect([Snapshot(Start.AddMinutes(1), 200, 0)]);
            store.Collect([Snapshot(Start.AddDays(1), 300, 0)]);
            maintenance.RunIfDue(store, now);
            database.Sql("CREATE TRIGGER no_repeated_maintenance BEFORE UPDATE ON collection_state BEGIN SELECT RAISE(ABORT, 'Maintenance repeated'); END;");
            Assert.Null(maintenance.RunIfDue(store, now.AddHours(1)));
        }
        using (var restarted = new TrafficStore(database.Path))
        {
            var skipped = database.Maintenance().RunIfDue(restarted, now.AddHours(2));
            Assert.Equal(0, skipped?.RawMinutesArchived);
            database.Sql("DROP TRIGGER no_repeated_maintenance;");
            var nextDay = maintenance.RunIfDue(restarted, new DateTimeOffset(now.Date.AddDays(1), TimeSpan.Zero));
            Assert.Equal(1, nextDay?.RawMinutesArchived);
            Assert.Equal(200, Assert.Single(restarted.ReadTotalReportRows(now.AddDays(1))).TotalBytes);
            Assert.Single(restarted.ReadBuckets(Start), bucket => !bucket.IsDailyArchive);
        }
    }

    [Fact]
    public void InterruptedArchivingRollsBackAndRetriesWithoutDoubleCounting()
    {
        using var database = new TestDatabase(2);
        using var store = new TrafficStore(database.Path);
        Seed(store);
        var now = Start.AddDays(3);
        var maintenance = database.Maintenance();
        database.Sql("CREATE TRIGGER interrupt_retention BEFORE DELETE ON traffic_minute BEGIN SELECT RAISE(ABORT, 'Interrupted retention'); END;");

        Assert.Throws<SqliteException>(() => maintenance.RunIfDue(store, now));

        Assert.All(store.ReadBuckets(DateTimeOffset.UnixEpoch), bucket => Assert.False(bucket.IsDailyArchive));
        Assert.Equal(900, Assert.Single(store.ReadTotalReportRows(now)).TotalBytes);
        Assert.Equal(Start, store.ReadCollectionHistory(Start, now, TimeZoneInfo.Utc).RawHistoryFromUtc);
        database.Sql("DROP TRIGGER interrupt_retention;");
        Assert.Equal(2, maintenance.RunIfDue(store, now)?.RawMinutesArchived);
        Assert.Equal(900, Assert.Single(store.ReadTotalReportRows(now)).TotalBytes);
        Assert.Equal(4, store.ReadCollectionHistory(Start, now, TimeZoneInfo.Utc).Days.Sum(day => day.Observations));
        Assert.Equal(0, database.Maintenance().RunIfDue(store, now)?.RawMinutesArchived);
        Assert.True(store.CheckIntegrity().IsHealthy);
    }

    [Fact]
    public void OldRowsAreArchivedEvenWhenLegacyCoverageStartsRecently()
    {
        using var database = new TestDatabase(2);
        using var store = new TrafficStore(database.Path);
        Seed(store);
        var now = Start.AddDays(3);
        database.Sql($"UPDATE collection_state SET raw_history_from_utc = '{now.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}'; DELETE FROM collection_sample;");

        var result = database.Maintenance().RunIfDue(store, now);

        Assert.Equal(2, result?.RawMinutesArchived);
        Assert.Equal(900, Assert.Single(store.ReadTotalReportRows(now)).TotalBytes);
        Assert.Single(store.ReadBuckets(DateTimeOffset.UnixEpoch), bucket => bucket.IsDailyArchive);
    }

    [Fact]
    public async Task ConcurrentMaintenanceArchivesEachIntervalOnlyOnce()
    {
        using var database = new TestDatabase(2);
        using var first = new TrafficStore(database.Path);
        using var second = new TrafficStore(database.Path);
        Seed(first);
        using var start = new ManualResetEventSlim();
        var now = Start.AddDays(3);
        Task<RetentionResult?> Run(TrafficStore store) => Task.Run(() =>
        {
            Assert.True(start.Wait(TimeSpan.FromSeconds(10)));
            return database.Maintenance().RunIfDue(store, now);
        });
        var writers = new[] { Run(first), Run(second) };
        start.Set();
        var results = await Task.WhenAll(writers);

        Assert.Equal(2, results.Sum(result => result?.RawMinutesArchived ?? 0));
        Assert.Equal(900, Assert.Single(first.ReadTotalReportRows(now)).TotalBytes);
        Assert.Equal(4, first.ReadCollectionHistory(Start, now, TimeZoneInfo.Utc).Days.Sum(day => day.Observations));
        Assert.True(first.CheckIntegrity().IsHealthy);
    }

    [Theory]
    [InlineData(3, 8)]
    [InlineData(11, 1)]
    public void LocalDateSchedulingHandlesShortAndLongDstDays(int month, int day)
    {
        using var database = new TestDatabase(1);
        using var store = new TrafficStore(database.Path);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var date = new DateTime(2026, month, day);
        DateTimeOffset Utc(DateTime local) => new(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
        var old = Utc(date.AddDays(-2).AddHours(12));
        store.Collect([Snapshot(old, 100, 0)]);
        store.Collect([Snapshot(old.AddMinutes(1), 200, 0)]);
        store.Collect([Snapshot(Utc(date.AddDays(-1).AddHours(12)), 300, 0)]);
        store.Collect([Snapshot(Utc(date), 400, 0)]);
        var maintenance = database.Maintenance(zone);

        Assert.Equal(1, maintenance.RunIfDue(store, Utc(date.AddMinutes(15)))?.RawMinutesArchived);
        Assert.Null(maintenance.RunIfDue(store, Utc(date.AddHours(23).AddMinutes(59))));
        Assert.Equal(1, maintenance.RunIfDue(store, Utc(date.AddDays(1).AddMinutes(15)))?.RawMinutesArchived);

        var raw = Assert.Single(store.ReadBuckets(old), bucket => !bucket.IsDailyArchive);
        Assert.Equal(Utc(date), raw.MinuteUtc);
        Assert.Equal(300, Assert.Single(store.ReadTotalReportRows(Utc(date.AddDays(1)))).TotalBytes);
        Assert.Equal(4, store.ReadCollectionHistory(old, Utc(date.AddDays(1)), zone).Days.Sum(value => value.Observations));
    }

    [Fact]
    public void RestartAfterLongInterruptionCatchesUpWithoutLosingAggregates()
    {
        using var database = new TestDatabase(2);
        using (var original = new TrafficStore(database.Path))
        {
            Seed(original);
            database.Maintenance().RunIfDue(original, Start.AddDays(3));
        }
        using var resumed = new TrafficStore(database.Path);

        var result = database.Maintenance().RunIfDue(resumed, Start.AddDays(20));

        Assert.Equal(1, result?.RawMinutesArchived);
        Assert.All(resumed.ReadBuckets(DateTimeOffset.UnixEpoch), bucket => Assert.True(bucket.IsDailyArchive));
        Assert.Equal(900, Assert.Single(resumed.ReadTotalReportRows(Start.AddDays(20))).TotalBytes);
        Assert.Equal(4, resumed.ReadCollectionHistory(Start, Start.AddDays(20), TimeZoneInfo.Utc).Days.Sum(day => day.Observations));
        Assert.True(resumed.CheckIntegrity().IsHealthy);
    }

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static void Seed(TrafficStore store)
    {
        store.Collect([Snapshot(Start, 100, 100)]);
        store.Collect([Snapshot(Start.AddMinutes(1), 500, 300)]);
        store.Collect([Snapshot(Start.AddMinutes(2), 500, 300)]);
        store.Collect([Snapshot(Start.AddDays(2), 700, 400)]);
    }

    private static NetworkInterfaceSnapshot Snapshot(DateTimeOffset at, long received, long sent) => new(
        "wifi", "Wi-Fi", "Intel Wi-Fi", "Wireless80211", "Up", 1_000_000_000, received, sent, at);

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"octetledger-retention-{Guid.NewGuid():N}");
        private readonly string settingsPath;
        public string Path => System.IO.Path.Combine(directory, "test.db");

        public TestDatabase(int rawDays)
        {
            settingsPath = System.IO.Path.Combine(directory, "settings.json");
            OctetLedgerSettings.Update(settingsPath, current => current with { RetentionRawDays = rawDays });
        }

        public AutomaticRetention Maintenance(TimeZoneInfo? zone = null) => new(settingsPath, zone ?? TimeZoneInfo.Utc);

        public void Sql(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
