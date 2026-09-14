using OctetLedger.Core;
using Microsoft.Data.Sqlite;

namespace OctetLedger.Tests;

public class TrafficStoreTests
{
    [Fact]
    public void CollectStoresOnlyCounterDifferencesAfterBaseline()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(testDirectory, "test.db");

        try
        {
            using (var store = new TrafficStore(databasePath))
            {
                var first = store.Collect([Snapshot(1_000, 2_000, 0)]);
                var second = store.Collect([Snapshot(4_000, 3_500, 1)]);

                Assert.Equal(1, first.BaselinesCreated);
                Assert.Equal(0, first.BytesReceived);
                Assert.Equal(3_000, second.BytesReceived);
                Assert.Equal(1_500, second.BytesSent);

                var bucket = Assert.Single(store.ReadBuckets(DateTimeOffset.UnixEpoch));
                Assert.Equal(3_000, bucket.BytesReceived);
                Assert.Equal(1_500, bucket.BytesSent);
                Assert.Equal(60, bucket.IntervalSeconds);
                Assert.Equal(TrafficStore.CurrentSchemaVersion, store.ReadSchemaVersion());
            }
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void CollectTreatsDecreasedCountersAsAReset()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(testDirectory, "test.db");

        try
        {
            using (var store = new TrafficStore(databasePath))
            {
                store.Collect([Snapshot(5_000, 5_000, 0)]);

                var result = store.Collect([Snapshot(100, 200, 1)]);

                Assert.Equal(0, result.BytesReceived);
                Assert.Equal(0, result.BytesSent);
                Assert.Empty(store.ReadBuckets(DateTimeOffset.UnixEpoch));
            }
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void CollectRebaselinesBothDirectionsWhenEitherCounterResets()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(testDirectory, "test.db");
        try
        {
            using var store = new TrafficStore(databasePath);
            store.Collect([Snapshot(5_000, 5_000, 0)]);

            var reset = store.Collect([Snapshot(100, 7_000, 1)]);
            var resumed = store.Collect([Snapshot(600, 8_000, 2)]);

            Assert.Equal(0, reset.BytesReceived + reset.BytesSent);
            Assert.Equal(1_500, resumed.BytesReceived + resumed.BytesSent);
            var bucket = Assert.Single(store.ReadBuckets(DateTimeOffset.UnixEpoch));
            Assert.Equal(1_500, bucket.BytesReceived + bucket.BytesSent);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void CollectRebaselinesWhenObservationTimeDoesNotAdvance()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(testDirectory, "test.db");
        try
        {
            using var store = new TrafficStore(databasePath);
            store.Collect([Snapshot(1_000, 1_000, 2)]);

            var backwards = store.Collect([Snapshot(2_000, 2_000, 1)]);
            var resumed = store.Collect([Snapshot(3_000, 4_000, 3)]);

            Assert.Equal(0, backwards.BytesReceived + backwards.BytesSent);
            Assert.Equal(3_000, resumed.BytesReceived + resumed.BytesSent);
            Assert.Equal(120, Assert.Single(store.ReadBuckets(DateTimeOffset.UnixEpoch)).IntervalSeconds);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void CheckAndBackupProduceAHealthyIndependentDatabase()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(testDirectory, "source.db");
        var backupPath = Path.Combine(testDirectory, "backup.db");

        try
        {
            using (var store = new TrafficStore(databasePath))
            {
                store.Collect([Snapshot(1_000, 2_000, 0)]);
                store.Collect([Snapshot(4_000, 3_500, 1)]);
                Assert.True(store.CheckIntegrity().IsHealthy);
                Assert.Equal(backupPath, store.Backup(backupPath));
            }

            using var backup = new TrafficStore(backupPath);
            Assert.True(backup.CheckIntegrity().IsHealthy);
            var bucket = Assert.Single(backup.ReadBuckets(DateTimeOffset.UnixEpoch));
            Assert.Equal(3_000, bucket.BytesReceived);
            Assert.Equal(1_500, bucket.BytesSent);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void CollectPreservesLongObservationDurationAndSqlAggregatesTotal()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(testDirectory, "test.db");
        try
        {
            using var store = new TrafficStore(databasePath);
            store.Collect([Snapshot(1_000, 2_000, 0)]);
            store.Collect([Snapshot(7_000, 8_000, 10)]);

            var bucket = Assert.Single(store.ReadBuckets(DateTimeOffset.UnixEpoch));
            Assert.Equal(600, bucket.IntervalSeconds);
            var total = Assert.Single(store.ReadTotalReportRows(DateTimeOffset.UnixEpoch.AddHours(1)));
            Assert.Equal(20, total.PeakBytesPerSecond);
            Assert.Equal(12_000, total.TotalBytes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void CollectionRemainsExactAcrossMultipleDaysAndASleepGap()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(testDirectory, "test.db");
        try
        {
            using var store = new TrafficStore(databasePath);
            var received = 10_000L;
            var sent = 5_000L;
            var elapsedMinutes = 0;
            store.Collect([Snapshot(received, sent, elapsedMinutes)]);

            const int samples = 288;
            for (var sample = 1; sample <= samples; sample++)
            {
                elapsedMinutes += sample == 120 ? 495 : 15;
                received += 1_000;
                sent += 500;
                store.Collect([Snapshot(received, sent, elapsedMinutes)]);
            }

            var buckets = store.ReadBuckets(DateTimeOffset.UnixEpoch);
            Assert.Equal(samples, buckets.Count);
            Assert.Equal(samples * 1_500L, buckets.Sum(bucket => bucket.BytesReceived + bucket.BytesSent));
            Assert.Equal(495d * 60, buckets.Max(bucket => bucket.LongestIntervalSeconds));
            var status = store.GetStatus();
            Assert.Equal((long)samples, status.StoredMinutes);
            Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(elapsedMinutes), status.LastCollectionUtc);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void OpeningVersionOneDatabaseMigratesIntervalDurationWithoutLosingTraffic()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(testDirectory, "legacy.db");
        Directory.CreateDirectory(testDirectory);
        try
        {
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE adapter_state (
                        interface_id TEXT PRIMARY KEY, name TEXT NOT NULL, description TEXT NOT NULL,
                        type TEXT NOT NULL, last_received INTEGER NOT NULL, last_sent INTEGER NOT NULL,
                        last_seen_utc TEXT NOT NULL);
                    CREATE TABLE traffic_minute (
                        interface_id TEXT NOT NULL, minute_utc TEXT NOT NULL,
                        bytes_received INTEGER NOT NULL, bytes_sent INTEGER NOT NULL,
                        PRIMARY KEY (interface_id, minute_utc));
                    INSERT INTO adapter_state VALUES ('wifi', 'Wi-Fi', 'Wi-Fi', 'Wireless80211', 100, 50, '2026-01-01T00:01:00.0000000+00:00');
                    INSERT INTO traffic_minute VALUES ('wifi', '2026-01-01T00:01:00.0000000+00:00', 100, 50);
                    PRAGMA user_version = 1;
                    """;
                command.ExecuteNonQuery();
            }

            using (var store = new TrafficStore(databasePath))
            {
                var bucket = Assert.Single(store.ReadBuckets(DateTimeOffset.UnixEpoch));
                Assert.Equal(60, bucket.IntervalSeconds);
                Assert.Equal(150, bucket.BytesReceived + bucket.BytesSent);
                Assert.Equal(TrafficStore.CurrentSchemaVersion, store.ReadSchemaVersion());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CollectionRetriesThroughATemporaryExclusiveDatabaseLock()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(testDirectory, "test.db");
        try
        {
            using (var baseline = new TrafficStore(databasePath)) baseline.Collect([Snapshot(1_000, 2_000, 0)]);
            using var blocker = new SqliteConnection($"Data Source={databasePath}");
            blocker.Open();
            using var lockCommand = blocker.CreateCommand();
            lockCommand.CommandText = "BEGIN EXCLUSIVE;";
            lockCommand.ExecuteNonQuery();

            var collection = Task.Run(() =>
            {
                using var store = new TrafficStore(databasePath);
                return store.Collect([Snapshot(2_000, 3_000, 1)]);
            });
            await Task.Delay(200);
            using var unlock = blocker.CreateCommand();
            unlock.CommandText = "COMMIT;";
            unlock.ExecuteNonQuery();

            var result = await collection;
            Assert.Equal(2_000, result.BytesReceived + result.BytesSent);
            blocker.Dispose();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void SwitchingInterfacesCreatesIndependentBaselinesAndHistory()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(testDirectory, "test.db");
        try
        {
            using var store = new TrafficStore(databasePath);
            store.Collect([Snapshot("wifi", 1_000, 1_000, 0)]);
            store.Collect([Snapshot("ethernet", 5_000, 5_000, 1)]);
            store.Collect([Snapshot("wifi", 2_000, 3_000, 2), Snapshot("ethernet", 6_000, 7_000, 2)]);

            var buckets = store.ReadBuckets(DateTimeOffset.UnixEpoch);
            Assert.Equal(2, buckets.Count);
            Assert.Contains(buckets, bucket => bucket.InterfaceId == "wifi" && bucket.BytesReceived + bucket.BytesSent == 3_000);
            Assert.Contains(buckets, bucket => bucket.InterfaceId == "ethernet" && bucket.BytesReceived + bucket.BytesSent == 3_000);
        }
        finally
        {
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void RestoreRejectsCorruptSourceAndPreservesDestination()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var destination = Path.Combine(testDirectory, "active.db");
        var corrupt = Path.Combine(testDirectory, "corrupt.db");
        try
        {
            using (var store = new TrafficStore(destination)) store.Collect([Snapshot(1_000, 2_000, 0)]);
            File.WriteAllText(corrupt, "not a sqlite database");

            Assert.Throws<InvalidDataException>(() => TrafficStore.Restore(corrupt, destination));

            Assert.True(TrafficStore.CheckIntegrity(destination).IsHealthy);
        }
        finally
        {
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void RestoreRejectsHealthyDatabaseFromAnotherApplication()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var destination = Path.Combine(testDirectory, "active.db");
        var unrelated = Path.Combine(testDirectory, "unrelated.db");
        try
        {
            using (var store = new TrafficStore(destination)) store.Collect([Snapshot(1_000, 2_000, 0)]);
            using (var connection = new SqliteConnection($"Data Source={unrelated}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE unrelated_data (value TEXT NOT NULL);";
                command.ExecuteNonQuery();
            }

            var check = TrafficStore.CheckIntegrity(unrelated);

            Assert.False(check.IsHealthy);
            Assert.Contains(check.Messages, message => message.Contains("required OctetLedger", StringComparison.OrdinalIgnoreCase) ||
                                                       message.Contains("do not belong", StringComparison.OrdinalIgnoreCase));
            Assert.Throws<InvalidDataException>(() => TrafficStore.Restore(unrelated, destination));
            Assert.True(TrafficStore.CheckIntegrity(destination).IsHealthy);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void OpeningDatabaseFromNewerVersionFailsWithoutChangingItsVersion()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(testDirectory, "future.db");
        try
        {
            using (var store = new TrafficStore(databasePath)) { }
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA user_version = {TrafficStore.CurrentSchemaVersion + 1};";
                command.ExecuteNonQuery();
            }

            Assert.Throws<InvalidDataException>(() => new TrafficStore(databasePath));
            using var inspection = new SqliteConnection($"Data Source={databasePath}");
            inspection.Open();
            using var version = inspection.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.Equal(TrafficStore.CurrentSchemaVersion + 1,
                Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void RestorePreservesPreviousDatabaseAsBackup()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var destination = Path.Combine(testDirectory, "active.db");
        var source = Path.Combine(testDirectory, "backup.db");
        try
        {
            using (var active = new TrafficStore(destination)) active.Collect([Snapshot(1_000, 1_000, 0)]);
            using (var backup = new TrafficStore(source))
            {
                backup.Collect([Snapshot(5_000, 5_000, 0)]);
                backup.Collect([Snapshot(7_000, 8_000, 1)]);
            }

            var result = TrafficStore.Restore(source, destination);

            Assert.NotNull(result.PreviousDatabaseBackupPath);
            Assert.True(File.Exists(result.PreviousDatabaseBackupPath));
            using var restored = new TrafficStore(destination);
            Assert.Equal(5_000, Assert.Single(restored.ReadBuckets(DateTimeOffset.UnixEpoch)).BytesReceived +
                                  Assert.Single(restored.ReadBuckets(DateTimeOffset.UnixEpoch)).BytesSent);
        }
        finally
        {
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void RetentionArchivesOldMinutesWithoutChangingTotals()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(testDirectory, "test.db");
        try
        {
            using var store = new TrafficStore(databasePath);
            store.Collect([Snapshot(1_000, 2_000, 0)]);
            store.Collect([Snapshot(4_000, 3_500, 1)]);
            var before = Assert.Single(store.ReadTotalReportRows(DateTimeOffset.UtcNow)).TotalBytes;

            var result = store.ApplyRetention(30, DateTimeOffset.UtcNow);

            Assert.Equal(1, result.RawMinutesArchived);
            Assert.Equal(before, Assert.Single(store.ReadTotalReportRows(DateTimeOffset.UtcNow)).TotalBytes);
            Assert.Equal(before, Assert.Single(store.ReadBuckets(DateTimeOffset.UnixEpoch)).BytesReceived +
                                 Assert.Single(store.ReadBuckets(DateTimeOffset.UnixEpoch)).BytesSent);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void ApplicationTrafficIsAggregatedByProcess()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(testDirectory, "test.db");
        try
        {
            ApplicationTrafficStore.Add(DateTimeOffset.UtcNow, [new ApplicationTrafficRow("browser.exe", 100, 50)], databasePath);
            ApplicationTrafficStore.Add(DateTimeOffset.UtcNow, [new ApplicationTrafficRow("browser.exe", 25, 75)], databasePath);

            var row = Assert.Single(ApplicationTrafficStore.ReadTop(DateTimeOffset.UtcNow.AddDays(-1), 10, databasePath));
            Assert.Equal(125, row.BytesReceived);
            Assert.Equal(125, row.BytesSent);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static NetworkInterfaceSnapshot Snapshot(long received, long sent, int minute)
        => Snapshot("test-interface", received, sent, minute);

    private static NetworkInterfaceSnapshot Snapshot(string id, long received, long sent, int minute)
    {
        return new NetworkInterfaceSnapshot(
            id,
            "Test adapter",
            "Test adapter",
            "Ethernet",
            "Up",
            1_000_000_000,
            received,
            sent,
            DateTimeOffset.UnixEpoch.AddMinutes(minute));
    }
}
