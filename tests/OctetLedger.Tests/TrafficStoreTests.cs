using OctetLedger.Core;

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
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static NetworkInterfaceSnapshot Snapshot(long received, long sent, int minute)
    {
        return new NetworkInterfaceSnapshot(
            "test-interface",
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
