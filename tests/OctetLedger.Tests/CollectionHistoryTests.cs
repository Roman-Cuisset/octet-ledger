using Microsoft.Data.Sqlite;
using OctetLedger.Core;

namespace OctetLedger.Tests;

public sealed class CollectionHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EmptyHistoryAndBaselineAreNotMeasuredZeroButAValidZeroIntervalIsStored()
    {
        using var database = new TestDatabase();
        using var store = new TrafficStore(database.Path);
        var empty = store.ReadCollectionHistory(Start, Start.AddMinutes(2));
        Assert.Null(empty.LastSuccessUtc);
        Assert.Empty(empty.Days);
        Assert.Contains(empty.Gaps, gap => gap.Reason == "unknown_coverage");

        store.Collect([Snapshot(0)]);
        Assert.Empty(store.ReadBuckets(DateTimeOffset.UnixEpoch));
        var baseline = Assert.Single(store.ReadCollectionHistory(Start, Start.AddMinutes(1)).Days);
        Assert.Equal(1, baseline.Observations);
        Assert.Equal(0, baseline.MeasuredIntervals);
        Assert.Equal(0, baseline.ZeroTrafficIntervals);

        store.Collect([Snapshot(1)]);
        var zero = Assert.Single(store.ReadBuckets(DateTimeOffset.UnixEpoch));
        Assert.Equal(0, zero.BytesReceived + zero.BytesSent);
        Assert.Equal(60, zero.IntervalSeconds);
        var day = Assert.Single(store.ReadCollectionHistory(Start, Start.AddMinutes(2)).Days);
        Assert.Equal(2, day.Observations);
        Assert.Equal(1, day.MeasuredIntervals);
        Assert.Equal(1, day.ZeroTrafficIntervals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongGapIsRecordedEvenWhenResumedCountersAreZeroOrReset(bool reset)
    {
        using var database = new TestDatabase();
        using var store = new TrafficStore(database.Path);
        store.Collect([Snapshot(0, 1_000, 2_000)]);
        store.Collect([Snapshot(10, reset ? 10 : 1_000, reset ? 20 : 2_000)]);
        var history = store.ReadCollectionHistory(Start.AddMinutes(5), Start.AddMinutes(11));
        var interruption = Assert.Single(history.Gaps, gap => gap.Reason == "unknown_collection_gap");
        Assert.Equal(Start.AddMinutes(1), interruption.StartUtc);
        Assert.Equal(Start.AddMinutes(10), interruption.EndUtc);
        if (reset)
        {
            Assert.Empty(store.ReadBuckets(DateTimeOffset.UnixEpoch));
            Assert.Contains(history.Gaps, gap => gap.Reason == "counter_reset" && gap.InterfaceId == "wifi");
        }
        else
        {
            Assert.Equal(600, Assert.Single(store.ReadBuckets(DateTimeOffset.UnixEpoch)).LongestIntervalSeconds);
            Assert.Equal(1, Assert.Single(history.Days).ZeroTrafficIntervals);
        }
        store.Collect([Snapshot(11, reset ? 110 : 1_100, reset ? 70 : 2_050)]);
        Assert.Equal(150, store.ReadBuckets(DateTimeOffset.UnixEpoch).Sum(bucket => bucket.BytesReceived + bucket.BytesSent));
        Assert.DoesNotContain(store.ReadCollectionHistory(Start, Start.AddMinutes(12)).Gaps, gap => gap.EndUtc is null);
    }

    [Fact]
    public void DisconnectedFramesAreCommittedWithCurrentOpenGapsAndRecoverHonestly()
    {
        using var database = new TestDatabase();
        using var store = new TrafficStore(database.Path);
        store.Collect([Snapshot(0)]);
        store.Collect([Snapshot(1) with { Status = "Down" }]);
        store.Collect([], Start.AddMinutes(2));
        var disconnected = store.ReadCollectionHistory(Start, Start.AddMinutes(3));
        Assert.Equal(Start.AddMinutes(2), disconnected.LastSuccessUtc);
        Assert.Equal(disconnected.LastSuccessUtc, store.GetStatus().LastCollectionUtc);
        Assert.Equal(2, Assert.Single(disconnected.Days).NoActiveInterfaceObservations);
        Assert.Equal(0, Assert.Single(disconnected.Days).MeasuredIntervals);
        Assert.Contains(disconnected.Gaps, gap => gap.Reason == "interface_not_up" && gap.InterfaceId == "wifi" && gap.EndUtc is null);
        Assert.Contains(disconnected.Gaps, gap => gap.Reason == "no_active_interfaces" && gap.EndUtc is null);
        Assert.Empty(store.ReadBuckets(DateTimeOffset.UnixEpoch));

        store.Collect([Snapshot(3, 100, 50)]);
        var recovered = store.ReadCollectionHistory(Start, Start.AddMinutes(4));
        Assert.All(recovered.Gaps, gap => Assert.NotNull(gap.EndUtc));
        Assert.Equal(150, Assert.Single(store.ReadBuckets(DateTimeOffset.UnixEpoch)).BytesReceived +
            Assert.Single(store.ReadBuckets(DateTimeOffset.UnixEpoch)).BytesSent);
        Assert.Contains(recovered.Gaps, gap => gap.Reason == "interface_not_up" && gap.EndUtc == Start.AddMinutes(3));
    }

    [Fact]
    public void SwitchingInterfacesDoesNotTreatNewBaselineAsZeroOrDiscardOldInterfaceVolume()
    {
        using var database = new TestDatabase();
        using var store = new TrafficStore(database.Path);
        store.Collect([Snapshot(0, 1_000, 1_000)]);
        store.Collect([Snapshot(1, 9_000, 9_000) with { Id = "ethernet", Name = "Ethernet" }]);
        var missing = Assert.Single(store.ReadCollectionHistory(Start, Start.AddMinutes(2)).Gaps,
            gap => gap.Reason == "interface_not_observed");
        Assert.Equal("wifi", missing.InterfaceId);
        Assert.Null(missing.EndUtc);
        store.Collect([Snapshot(2, 1_100, 1_200), Snapshot(2, 9_400, 9_500) with { Id = "ethernet", Name = "Ethernet" }]);
        var history = store.ReadCollectionHistory(Start.AddMinutes(1), Start.AddMinutes(3));
        var day = Assert.Single(history.Days);
        Assert.Equal(2, day.Observations);
        Assert.Equal(2, day.MeasuredIntervals);
        Assert.Equal(0, day.ZeroTrafficIntervals);
        Assert.Equal(1_200, store.ReadBuckets(DateTimeOffset.UnixEpoch).Sum(bucket => bucket.BytesReceived + bucket.BytesSent));
        Assert.Contains(history.Gaps, gap => gap.InterfaceId == "wifi" && gap.EndUtc == Start.AddMinutes(2));
    }

    [Fact]
    public void CurrentUnknownGapUsesPreviousSampleOutsideRequestedRangeAndClosesAfterCollection()
    {
        using var database = new TestDatabase();
        using var store = new TrafficStore(database.Path);
        store.Collect([Snapshot(0)]);
        var gap = Assert.Single(store.ReadCollectionHistory(Start.AddMinutes(3), Start.AddMinutes(5)).Gaps);
        Assert.Equal("unknown_collection_gap", gap.Reason);
        Assert.Equal(Start.AddMinutes(1), gap.StartUtc);
        Assert.Null(gap.EndUtc);
        store.Collect([Snapshot(5, 100, 0)]);
        var recovered = Assert.Single(store.ReadCollectionHistory(Start.AddMinutes(3), Start.AddMinutes(6)).Gaps);
        Assert.Equal(gap.StartUtc, recovered.StartUtc);
        Assert.Equal(Start.AddMinutes(5), recovered.EndUtc);
        Assert.Equal(100, Assert.Single(store.ReadBuckets(DateTimeOffset.UnixEpoch)).BytesReceived);
    }

    [Fact]
    public void ClockRollbackAndOneDirectionResetRebaselineWithoutNegativeIntervals()
    {
        using var database = new TestDatabase();
        using var store = new TrafficStore(database.Path);
        store.Collect([Snapshot(2, 1_000, 1_000)]);
        store.Collect([Snapshot(1, 2_000, 2_000)]);
        store.Collect([Snapshot(3, 3_000, 4_000)]);
        store.Collect([Snapshot(4, 100, 5_000)]);
        store.Collect([Snapshot(5, 200, 5_100)]);
        var buckets = store.ReadBuckets(DateTimeOffset.UnixEpoch);
        Assert.All(buckets, bucket => Assert.True(bucket.IntervalSeconds > 0));
        Assert.Equal(3_200, buckets.Sum(bucket => bucket.BytesReceived + bucket.BytesSent));
        var history = store.ReadCollectionHistory(Start, Start.AddMinutes(6));
        Assert.Contains(history.Gaps, gap => gap.Reason == "clock_not_advanced");
        Assert.Contains(history.Gaps, gap => gap.Reason == "counter_reset");
        Assert.All(history.Gaps.Where(gap => gap.EndUtc is not null), gap => Assert.True(gap.EndUtc >= gap.StartUtc));
        Assert.Equal(2, history.Days.Sum(day => day.MeasuredIntervals));
    }

    [Theory]
    [InlineData(3, 8, -5, -4)]
    [InlineData(11, 1, -4, -5)]
    public void DstTransitionUsesElapsedUtcTimeAndOneLocalDay(int month, int day, int beforeOffset, int afterOffset)
    {
        using var database = new TestDatabase();
        using var store = new TrafficStore(database.Path);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var before = new DateTimeOffset(2026, month, day, 1, 59, 0, TimeSpan.FromHours(beforeOffset));
        var after = month == 3
            ? new DateTimeOffset(2026, month, day, 3, 0, 0, TimeSpan.FromHours(afterOffset))
            : new DateTimeOffset(2026, month, day, 1, 0, 0, TimeSpan.FromHours(afterOffset));
        store.Collect([Snapshot(0) with { CapturedAt = before }]);
        store.Collect([Snapshot(1, 100, 50) with { CapturedAt = after }]);
        Assert.Equal(60, Assert.Single(store.ReadBuckets(DateTimeOffset.UnixEpoch)).IntervalSeconds);
        var history = store.ReadCollectionHistory(before, after.AddMinutes(1), zone);
        Assert.Empty(history.Gaps);
        Assert.Equal($"2026-{month:00}-{day:00}", Assert.Single(history.Days).Period);
        Assert.Equal(1, Assert.Single(history.Days).MeasuredIntervals);
    }

    [Fact]
    public void RetentionPreservesZeroCountsAndTotalsButNotClaimsOfRawCoverage()
    {
        using var database = new TestDatabase();
        using var store = new TrafficStore(database.Path);
        store.Collect([Snapshot(0)]);
        store.Collect([Snapshot(1)]);
        store.Collect([Snapshot(2, 100, 50)]);
        var cutoff = Start.AddDays(1).Date;
        var now = new DateTimeOffset(cutoff.AddDays(1), TimeSpan.Zero);
        store.Collect([Snapshot(0, 200, 100) with { CapturedAt = new DateTimeOffset(cutoff, TimeSpan.Zero) }]);
        var before = Assert.Single(store.ReadTotalReportRows(now));
        store.ApplyRetention(1, now, TimeZoneInfo.Utc);
        var total = Assert.Single(store.ReadTotalReportRows(now));
        Assert.Equal(before.TotalBytes, total.TotalBytes);
        Assert.Equal("mixed", total.Precision);
        Assert.Equal("mixed", Assert.Single(TrafficReport.Total(store.ReadBuckets(DateTimeOffset.UnixEpoch))).Precision);
        var history = store.ReadCollectionHistory(Start, now, TimeZoneInfo.Utc);
        Assert.Equal(4, history.Days.Sum(day => day.Observations));
        Assert.Equal(1, history.Days.Sum(day => day.ZeroTrafficIntervals));
        Assert.Equal("daily_archives", history.Days[0].Precision);
        Assert.Equal("raw_intervals", history.Days[1].Precision);
        Assert.Equal(new DateTimeOffset(cutoff, TimeSpan.Zero), history.RawHistoryFromUtc);
        Assert.Contains(history.Gaps, gap => gap.Reason == "unknown_coverage");
        Assert.Equal(0, store.ApplyRetention(1, now, TimeZoneInfo.Utc).RawMinutesArchived);
        Assert.Equal(4, store.ReadCollectionHistory(Start, now, TimeZoneInfo.Utc).Days.Sum(day => day.Observations));
        using var connection = new SqliteConnection($"Data Source={database.Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM collection_sample;";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Theory]
    [InlineData(3, 8)]
    [InlineData(11, 1)]
    public void RetentionUsesLocalDayBoundaryAcrossDstAndPreservesArchivedZeroObservations(int month, int day)
    {
        using var database = new TestDatabase();
        using var store = new TrafficStore(database.Path);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var localDay = new DateTime(2026, month, day);
        var first = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localDay, zone), TimeSpan.Zero);
        var boundary = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localDay.AddDays(1), zone), TimeSpan.Zero);
        var now = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localDay.AddDays(2), zone), TimeSpan.Zero);
        store.Collect([Snapshot(0) with { CapturedAt = first }]);
        store.Collect([Snapshot(1) with { CapturedAt = first.AddMinutes(1) }]);
        store.Collect([Snapshot(2, 100, 50) with { CapturedAt = boundary }]);

        store.ApplyRetention(1, now, zone);

        var history = store.ReadCollectionHistory(first, now, zone);
        Assert.Equal(3, history.Days.Sum(value => value.Observations));
        var archived = Assert.Single(history.Days, value => value.Precision == "daily_archives");
        Assert.Equal(localDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), archived.Period);
        Assert.Equal(2, archived.Observations);
        Assert.Equal(1, archived.ZeroTrafficIntervals);
        Assert.Equal(boundary, history.RawHistoryFromUtc);
        Assert.Equal(150, Assert.Single(store.ReadTotalReportRows(now)).TotalBytes);
        Assert.Contains(store.ReadBuckets(first), bucket => bucket.IsDailyArchive && bucket.MinuteUtc == first);
        store.ApplyRetention(1, now.AddDays(2), zone);
        Assert.Equal("daily_archives", Assert.Single(store.ReadTotalReportRows(now)).Precision);
    }

    [Fact]
    public void VersionFiveMigrationPreservesBytesAndLeavesLegacyCoverageUnknown()
    {
        using var database = new TestDatabase();
        using (var connection = new SqliteConnection($"Data Source={database.Path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE adapter_state (interface_id TEXT PRIMARY KEY, name TEXT NOT NULL,
                    description TEXT NOT NULL, type TEXT NOT NULL, last_received INTEGER NOT NULL,
                    last_sent INTEGER NOT NULL, last_seen_utc TEXT NOT NULL);
                CREATE TABLE traffic_minute (interface_id TEXT NOT NULL, minute_utc TEXT NOT NULL,
                    bytes_received INTEGER NOT NULL, bytes_sent INTEGER NOT NULL,
                    interval_seconds REAL NOT NULL DEFAULT 60, peak_bytes_per_second REAL NOT NULL DEFAULT 0,
                    longest_interval_seconds REAL NOT NULL DEFAULT 60, PRIMARY KEY(interface_id, minute_utc));
                CREATE TABLE traffic_archive_day (interface_id TEXT NOT NULL, day_utc TEXT NOT NULL,
                    bytes_received INTEGER NOT NULL, bytes_sent INTEGER NOT NULL, interval_seconds REAL NOT NULL,
                    peak_bytes_per_second REAL NOT NULL, longest_interval_seconds REAL NOT NULL,
                    PRIMARY KEY(interface_id, day_utc));
                CREATE TABLE application_traffic_hour (process_name TEXT NOT NULL, hour_utc TEXT NOT NULL,
                    bytes_received INTEGER NOT NULL, bytes_sent INTEGER NOT NULL, PRIMARY KEY(process_name, hour_utc));
                INSERT INTO adapter_state VALUES ('wifi', 'Wi-Fi', 'Wi-Fi', 'Wireless80211', 100, 50, '2026-01-01T12:00:00.0000000+00:00');
                INSERT INTO traffic_minute VALUES ('wifi', '2026-01-01T12:00:00.0000000+00:00', 100, 50, 60, 2.5, 60);
                PRAGMA user_version = 5;
                """;
            command.ExecuteNonQuery();
        }
        using var store = new TrafficStore(database.Path);
        Assert.Equal(TrafficStore.CurrentSchemaVersion, store.ReadSchemaVersion());
        Assert.Equal(150, Assert.Single(store.ReadTotalReportRows(Start)).TotalBytes);
        var unknown = store.ReadCollectionHistory(Start.AddDays(-1), Start.AddMinutes(1));
        Assert.Null(unknown.TrackingStartedUtc);
        Assert.Null(unknown.LastSuccessUtc);
        Assert.Empty(unknown.Days);
        Assert.Equal("unknown_coverage", Assert.Single(unknown.Gaps).Reason);
        store.Collect([Snapshot(1, 200, 100)]);
        Assert.Equal(300, Assert.Single(store.ReadTotalReportRows(Start.AddMinutes(2))).TotalBytes);
        Assert.Equal(Start.AddMinutes(1), store.ReadCollectionHistory(Start, Start.AddMinutes(2)).TrackingStartedUtc);
        Assert.True(store.CheckIntegrity().IsHealthy);
    }

    [Fact]
    public void HistoryFailureRollsBackTrafficAndCounterBaselineTogether()
    {
        using var database = new TestDatabase();
        using var store = new TrafficStore(database.Path);
        store.Collect([Snapshot(0)]);
        using var connection = new SqliteConnection($"Data Source={database.Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_sample BEFORE INSERT ON collection_sample BEGIN SELECT RAISE(ABORT, 'test failure'); END;";
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => store.Collect([Snapshot(1, 100, 50)]));
        Assert.Empty(store.ReadBuckets(DateTimeOffset.UnixEpoch));
        Assert.Equal(Start, store.GetStatus().LastCollectionUtc);
        command.CommandText = "DROP TRIGGER reject_sample;";
        command.ExecuteNonQuery();
        var resumed = store.Collect([Snapshot(2, 200, 100)]);
        Assert.Equal(300, resumed.BytesReceived + resumed.BytesSent);
        Assert.Equal(2, store.ReadCollectionHistory(Start, Start.AddMinutes(3)).Days.Sum(day => day.Observations));
    }

    private static NetworkInterfaceSnapshot Snapshot(int minute, long received = 0, long sent = 0) =>
        new("wifi", "Wi-Fi", "Wi-Fi", "Wireless80211", "Up", 1_000_000_000, received, sent, Start.AddMinutes(minute));

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        public TestDatabase() => Directory.CreateDirectory(directory);
        public string Path => System.IO.Path.Combine(directory, "history.db");
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
